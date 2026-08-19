# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo artık doğrulanmış GitHub `pull_request` teslimatlarında hem `opened` hem de `synchronize` action'larını dar bir `ReleaseRiskInput` nesnesine dönüştürür, deterministik bir risk değerlendirmesi üretir ve kabul sonucunu PostgreSQL'de kalıcılaştırır. Yeni `accepted` teslimatlar için aynı transaction'da sürümlü bir release-risk outbox envelope'u da oluşturur. Açıkça etkinleştirilen PostgreSQL outbox dispatcher bu satırları süreli claim/lease ile alıp Kafka'ya at-least-once yayımlar. Bağımsız Kafka consumer adapter'ı V1 record'u doğrular; açıkça etkinleştirilen inbox processor exact payload ile PostgreSQL'e idempotent kabulü tamamladıktan sonra ilgili Kafka offset'ini explicit commit eder. Ayrı Python AI açıklama servisi aynı V1 snapshot'ı sıkı biçimde doğrulayıp insan-okunur açıklama üretir. Açıkça etkinleştirilen .NET AI açıklama processor'ı accepted inbox satırlarını bounded claim/lease ile sahiplenir, retryable hataları sınırlı sayıda yeniden dener ve başarıyı ya da kalıcı terminal nedeni aynı `eventId` satırında birbirini ezemeyen sonuçlar olarak saklar. Operatör terminal işleri dar, salt-okunur failed-work sözleşmesinden inceleyebilir. Manual replay, ordering/latest-state, dashboard ve deploy henüz eklenmemiştir.

## Bu adımda ne yapıyoruz?

- V006 ile AI açıklama terminal zamanı, stabil hata kodu ve operatör nedenini doğrudan `release_risk_event_inbox` satırına ekliyoruz. Inbox primary key'i olan `event_id`, kabulden başarı/terminal sonuca kadar tek idempotency sınırıdır.
- Timeout, bağlantı, `408`, `429`, `5xx` ve beklenmeyen hataları retryable; response/event/request contract ihlalleri ile diğer non-success HTTP durumlarını doğrudan terminal olarak sınıflandırıyoruz.
- `AiExplanationProcessor:MaximumAttempts` claim sayısını `1–100` arasında sınırlar. Retryable hata son denemede sessizce drop edilmez; aktif owner kalıcı terminal kod/neden yazar. Son claim'den sonra crash olursa expiry sonrası store işi yeniden çağırmadan `attempt_limit_exhausted` olarak sonlandırır.
- Başarılı açıklama ile terminal failure aynı satırda DB constraint'leri ve fenced store predicate'leriyle karşılıklı dışlanır. Duplicate terminal update aynı `eventId` ve aynı kod/neden için idempotent başarıdır; stale owner mevcut sonucu değiştiremez.
- `release_risk_ai_explanation_failed_work` görünümü ve bounded `ReadFailedWorkAsync` store sorgusu yalnız terminal işin kimliğini, attempt sayısını, nedenini, kabul zamanını ve immutable V1 envelope'unu gösterir; replay mutasyonu veya API eklemez.
- Mevcut webhook kabulünü, outbox'ı, Kafka V1 record'unu, durable-accept-then-commit sırasını ve bağımsız Python servis sözleşmesini değiştirmiyoruz.

## Neden yapıyoruz?

Kalıcı bir provider/contract hatasını sonsuza kadar retry etmek hem maliyeti hem operasyonel gürültüyü sınırsız bırakır; deneme limitinde işi görünmez biçimde bırakmak ise kabul edilmiş event'i kaybetmiş gibi davranır. Bu adım mevcut ownership modelini koruyup her kabulü ya başarıya ya da açık, sorgulanabilir bir terminal sonuca bağlar. Sonuç ile kaynak snapshot aynı satırda kaldığı için açıklama yaşam döngüsü `eventId` üzerinden yarış güvenli olur ve deterministik risk gerçeği değişmez.

## Mimari kararlar

### Neden ham gövdeyi doğruluyoruz?

GitHub imzayı gönderdiği byte dizisi üzerinden üretir. JSON önce deserialize edilip yeniden oluşturulursa boşluklar, alan sırası veya karakter kodlaması değişebilir ve doğru imza reddedilebilir. Bu nedenle doğrulayıcı `Request.Body` akışını doğrudan HMAC hesabına verir.

Bu checkpoint'te aynı byte'ları imza doğrulamasından sonra JSON olarak okuyabilmek için ASP.NET Core request buffering etkinleştirilir. İmza yine ham akış üzerinden hesaplanır; akış daha sonra başa sarılıp ayrıştırılır. Framework küçük gövdeleri bellekte, eşik üzerindekileri geçici dosyada tutabilir.

Trade-off: istek iki kez okunur ve buffering ek bellek/disk I/O maliyeti getirir. Ayrıca JSON ağacının kendisi bellekte tutulur. Bu küçük kabul sınırı için sadedir; yoğun trafikte streaming ayrıştırma, açık payload boyutu sınırı ve ölçüm ayrıca değerlendirilmelidir.

### Genel envelope ile risk girdisi neden ayrı?

`VerifiedGitHubWebhook`, güvenlik ve taşıma sınırıdır; teslimat GUID'sini, olay adını ve değişmez JSON snapshot'ını taşımaya devam eder. `ReleaseRiskInput` ise ReleaseGuard'ın iş sınırıdır; GitHub'ın iç içe nesne adlarını sonraki risk koduna sızdırmaz. Böylece imza/HTTP ayrıntıları ile risk değerlendirme kavramları birbirine karışmaz.

`GitHubRiskInputMapper`, `pull_request` / `opened` kombinasyonunu `change_opened`, `pull_request` / `synchronize` kombinasyonunu `change_updated` girdisine çevirir. Action yalnızca çıktı `Kind` değerini seçer; repository ve pull request alanları iki action için tek ortak eşleme ve doğrulama yolundan geçer. Böylece ortak GitHub payload sözleşmesi iki ayrı mapper dalında tekrarlanmaz.

Ek JSON alanları yok sayılır; gerekli alanların türü ve temel sınırları açıkça doğrulanır. Desteklenmeyen bir event/action geçerli bir GitHub teslimatı olabileceği için `ignored` kabul edilir. Buna karşılık desteklendiğini söylediğimiz `opened` veya `synchronize` payload'ı eksikse sessizce veri uydurmak yerine `400` döner ve teslimat GUID'si rezerve edilmez.

Trade-off: ilk model risk için yararlı değişiklik büyüklüğünü taşır fakat dosya yolları, commit listesi, etiketler veya review bilgisi taşımaz. `202` yanıtındaki `riskInput` normalizasyonu, `riskAssessment` ise bu girdiden üretilen değerlendirmeyi görünür ve test edilebilir kılar; aynı iki nesne hem delivery satırında hem sürümlü outbox envelope'unda JSONB snapshot olarak saklanır. Yeni alanlar gerçek bir risk kuralı tarafından gerekçelendirilmeden modele eklenmeyecektir.

### `synchronize` neden bağımsız snapshot?

GitHub `synchronize` action'ı, pull request'in head dalına yeni commit'ler gönderildiğinde gelir. Bu checkpoint, teslimat anındaki ortak PR alanlarını ve toplu değişiklik sayılarını `change_updated` olarak ele alır. Her yeni delivery GUID'si kendi `SourceDeliveryId` değerini taşır ve önceki teslimatı okumadan mevcut payload üzerinden yeniden değerlendirilir. Aynı repository/change number çifti için art arda gelen iki yeni GUID bu nedenle iki ayrı snapshot ve iki ayrı risk sonucu üretebilir.

Bu akış event'leri sıralamaz, önceki snapshot'ı güncellemez ve “en güncel PR durumu” seçmez. Daha geç alınmış bir GitHub teslimatı uygulamaya daha erken ulaşabilir; bu servis yalnızca her isteğin kendi risk sonucunu üretip delivery GUID'siyle saklar. PostgreSQL instance'lar arası duplicate kararını koordine eder, fakat teslimatlar arası ordering veya latest-state anlamı eklemez. Dolayısıyla burada event ordering, latest-state veya exactly-once garantisi verilmez.

Trade-off: bağımsız snapshot semantiği mevcut stateless değerlendiriciyi ve dar webhook sınırını korur; ancak PR yaşam döngüsünü sorgulamak, snapshot'ları kronolojik bir görünümde birleştirmek veya güvenilir son durumu göstermek için ileride kalıcı bir model ile açık bir sıralama/version stratejisi gerekir.

### İlk risk değerlendirmesi nasıl çalışıyor?

`ReleaseRiskEvaluator` yalnızca `ReleaseRiskInput` alır ve `ReleaseRiskAssessment` döner. `change_opened` ve `change_updated` aynı mevcut risk kurallarından geçer; değerlendirici `Kind` değerine göre gizli bir skor farkı üretmez. Değerlendirici stateless'tir; yani önceki çağrıları hatırlamaz. I/O, saat ve rastgelelik kullanmadığı için yan etkisiz ve deterministiktir. Sonuçtaki faktör puanlarının toplamı doğrudan skordur.

İlk kural tablosu şöyledir:

| Sinyal | Gözlenen değer | Puan | Faktör kodu |
| --- | ---: | ---: | --- |
| Değişiklik genişliği | `0–4` dosya | `0` | Faktör yok |
| Değişiklik genişliği | `5–19` dosya | `+15` | `wider_change` |
| Değişiklik genişliği | `20+` dosya | `+30` | `broad_change` |
| Satır hareketi (`Additions + Deletions`) | `0–199` satır | `0` | Faktör yok |
| Satır hareketi (`Additions + Deletions`) | `200–999` satır | `+20` | `elevated_change_churn` |
| Satır hareketi (`Additions + Deletions`) | `1000+` satır | `+50` | `high_change_churn` |
| Hedef dal | Tam olarak `main` veya `master` | `+20` | `primary_target_branch` |

Dosya genişliği ve satır hareketi farklı boyutlardır: ilki kaç parçaya dokunulduğunu, ikincisi toplam değişiklik hacmini temsil eder. Yine de her boyutta yalnızca tek kademe uygulanır; örneğin 20 dosya hem `wider_change` hem `broad_change` puanı almaz. Eklenen ve silinen satırlar da ayrı puanlanmaz, önce `long` toplamına çevrilerek tek hareket sinyali olur. Bu hem çift sayımı hem de iki büyük `int` değeri toplanırken taşmayı önler. Azami puan `30 + 50 + 20 = 100` olduğu için skoru sonradan kırpan ve faktör toplamını belirsizleştiren bir clamp gerekmez.

| Skor | Risk seviyesi |
| ---: | --- |
| `0–29` | `low` |
| `30–64` | `medium` |
| `65–100` | `high` |

`Title`, `Author`, `HeadBranch` ve `IsDraft` bu ilk sürümde puanlanmaz; bu alanlardan güvenilir risk anlamı çıkarmak için henüz ürün kuralı yoktur. `main` / `master` faktörü de GitHub branch-protection bilgisini bildiğini iddia etmez, yalnızca yaygın ana dal adlandırmasını açık bir sezgisel kural olarak kullanır. Özel ana dal adı kullanan depolar bu faktörü almaz. Bu sınırlama, ileride depo politikası girdiye eklenene kadar kabul edilen trade-off'tur.

### Idempotency anahtarı ve sınırı

GitHub, `X-GitHub-Delivery` değerini teslimatı tanımlayan global benzersiz kimlik (GUID) olarak belgeler ve redelivery sırasında aynı değerin korunduğunu belirtir. Bu nedenle anahtar payload hash'i değil bu GUID'dir. Kaynaklar: [webhook header sözleşmesi](https://docs.github.com/en/webhooks/webhook-events-and-payloads) ve [webhook iyi uygulamaları](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks).

`github_webhook_deliveries.delivery_id` bir PostgreSQL primary key'idir. Uygulama önceden `SELECT` ile varlık kontrolü yapmaz; tek `INSERT ... ON CONFLICT DO NOTHING RETURNING delivery_id` çalıştırır. Targetless conflict seçimi hem primary key'i hem accepted-only outbox foreign key'i için gereken `(delivery_id, disposition)` unique index'ini yarış arbiter'ı olarak kapsar; PostgreSQL hangi unique index'i önce değerlendirirse değerlendirsin loser `23505` sızdırmaz. Yeni delivery yarışında kazanan transaction satırı ekler, diğerleri boş `RETURNING` sonucu üzerinden duplicate döner. Böylece check-then-insert zaman penceresi oluşmaz ve karar aynı process belleğine bağlı kalmaz.

Transaction önce delivery insert'ini çalıştırır. `RETURNING` yalnız kazanan transaction'a GUID verdiği için outbox insert'i sadece yeni ve `accepted` sonuçta çalışır; loser transaction check-then-insert yapmadan duplicate döner. Delivery, risk snapshot'ları ve outbox insert'i aynı transaction nesnesini kullanır. Outbox insert'i veya commit başarısızsa bütün transaction rollback olur ve istek `5xx` ile sonuçlanır; GitHub aynı GUID'yi yeniden deneyebilir. `202 Accepted` ancak commit tamamlandıktan sonra verilir.

Commit'in başarıyla tamamlandığı halde HTTP yanıtının istemciye ulaşmadığı belirsiz hata penceresinde redelivery `duplicate` olur ve mevcut outbox satırına ikinci bir satır eklenmez. Bu **exactly-once işleme veya yayın** değildir; yalnızca aynı PostgreSQL transaction'ındaki kalıcı kabul ve outbox handoff'u için atomikliktir. Kafka veya başka bir harici yan etki transaction içinde değildir.

`ignored` teslimatlar da `disposition = 'ignored'` ve null risk alanlarıyla saklanır. Bu seçim, doğrulanmış fakat bilinçli olarak desteklenmeyen bir teslimatın kabul edildiği gerçeğini restart ve instance'lar arasında korur. Saklanmasaydı aynı redelivery her seferinde yeni bir ignored kabul gibi görünürdü. Trade-off: desteklenmeyen event trafiği tabloyu büyütür; retention/archival politikası henüz yoktur.

### Outbox sözleşmesi ve veritabanı constraint'leri

V1 envelope sözleşmesi aşağıdaki yedi top-level alanla sınırlıdır:

| Alan | Değer / kaynak |
| --- | --- |
| `eventId` | Delivery GUID; outbox `event_id` primary key'iyle aynı |
| `eventType` | Sabit `releaseguard.release-risk-assessed` |
| `schemaVersion` | Sayısal `1` |
| `sourceProvider` | Bugünkü mapper'dan gelen `github` |
| `kind` | Bugünkü `change_opened` veya `change_updated` |
| `riskInput` | Delivery satırındaki `ReleaseRiskInput` JSON snapshot'ıyla eşit |
| `riskAssessment` | Delivery satırındaki `ReleaseRiskAssessment` JSON snapshot'ıyla eşit |

`ReleaseRiskOutboxEnvelope` bu alanları tek sırada, `JsonSerializerDefaults.Web` ile camelCase serileştirir; sabit bir örnek byte çıktısı birim testinde kilitlidir. PostgreSQL JSONB nesne sırasına anlam yüklemez fakat V002 constraint'i top-level alan kümesini, type/version/provider/kind değerlerini, event ID eşleşmesini ve snapshot nesnelerinin varlığını doğrular. Gelecekte sözleşme değişecekse mevcut v1 satırlarını sessizce yeniden yorumlamak yerine yeni şema sürümü ve migration gerekir.

`release_risk_outbox_messages.event_id` hem primary key hem delivery kimliğidir; bu seçim bir delivery için en fazla tek event'i ilave unique index olmadan garanti eder. Bileşik foreign key `(event_id, delivery_disposition)`, parent tablodaki `(delivery_id, disposition)` unique constraint'ine bağlanır ve outbox tarafındaki `delivery_disposition = 'accepted'` check'iyle ignored satıra outbox bağlanmasını DB seviyesinde engeller. Bunun maliyeti parent tabloda primary key'e kısmen tekrar eden küçük bir unique index'tir; accepted-only ilişkiyi deklaratif ve yarış güvenli tutmak için kabul edilir.

Foreign key `ON DELETE RESTRICT` kullanır. Bir delivery silindiğinde ilişkili event'in cascade ile sessizce kaybolması istenmez; ilerideki retention işi önce outbox yaşam döngüsünü açıkça sonlandırıp ardından iki kaydı bilinçli sırayla silmelidir. V002, V001'de zaten bulunan accepted satırları geriye dönük olarak outbox'a doldurmaz: yalnız yeni kod yoluyla V002 sonrasında kabul edilen delivery'ler event üretir.

### Kafka producer sözleşmesi ve teslim sınırı

`IReleaseRiskEventProducer.PublishAsync` yalnız bir `ReleaseRiskOutboxEnvelope` ve `CancellationToken` alır. Kafka adapter'ı event type'ın tam olarak `releaseguard.release-risk-assessed`, `schemaVersion` değerinin `1`, `eventId` ile `riskInput.sourceDeliveryId` değerlerinin ve top-level provider/kind ile snapshot değerlerinin eşleşmesini yeniden doğrular. Böylece record constructor doğrudan kullanılarak farklı bir event sözleşmesi bu topic'e yanlışlıkla gönderilemez; yeni risk alanı veya taşıma modeli eklenmez.

Kafka message sözleşmesi şöyledir:

| Kafka alanı | Değer |
| --- | --- |
| Topic | Yalnız `Kafka:Topic` yapılandırması; kod topic oluşturmaz veya fallback ad uydurmaz. |
| Key | `eventId` / delivery GUID, standart tireli küçük harf `D` biçiminde. |
| Value | `ReleaseRiskOutboxEnvelope.SerializeToUtf8Bytes()`; DB insert'inde kullanılan aynı camelCase V1 serializer'ının deterministik UTF-8 çıktısı. |
| Headers | Bu V1 adapter header eklemez; event type ve schema version zaten versioned value içindedir. |

PostgreSQL `jsonb` semantik JSON eşitliğini korur fakat kaynak whitespace/property-order byte'larını saklama sözü vermez. Producer kendisine verilen typed V1 envelope'u DB insert'iyle aynı serializer üzerinden ürettiği için pre-`jsonb` deterministik JSON byte'larıyla aynıdır. Gelecekteki dispatcher DB'den okuduğu JSONB'yi V1 tipe dönüştürüp aynı serializer ile yeniden üretmelidir; JSONB'nin metinsel sunumunu taşıma sözleşmesi saymamalıdır.

Adapter `acks=all`, delivery reports ve idempotent producer kullanır. `PublishAsync`, delivery report `Persisted` olmadan başarı dönmez. Topic auto-create kapalıdır; bilinmeyen topic, erişilemeyen broker veya broker reddi bounded delivery süresi sonunda `ProduceException` olarak çağırana taşınır. Uygulama hatayı yutmaz ve kendi retry döngüsünü çalıştırmaz. Client'ın internal retry sayısı `Kafka:MaximumRetries`, toplam teslim penceresi `Kafka:DeliveryTimeoutMilliseconds` ile sonludur; idempotence aynı producer oturumundaki internal retry tekrarlarını azaltır fakat PostgreSQL ile ortak transaction veya süreçler arası exactly-once sağlamaz.

Kafka tabanlı at-least-once akışta acknowledgement kaybolması veya çağıranın belirsiz hata sonrası tekrar denemesi aynı event key'iyle birden fazla record üretebilir. Key duplicate tespiti için kullanılabilir ama Kafka normal topic'inde benzersizlik constraint'i değildir. Adapter yalnız tek bounded publish çağrısının sonucunu bildirir; aşağıdaki dispatcher kalıcı yeniden deneme ve crash recovery yaşam döngüsünü bunun üzerine kurar.

Çağrı başlamadan iptal edilmiş token önce kontrol edilir ve record enqueue edilmez. Enqueue sonrasında bekleyiş iptal edilirse Confluent client yalnız dönen task'ın bekleyişini iptal eder; broker record'u yine kabul edebilir. Bu nedenle in-flight cancellation sonucu **belirsiz** kabul edilir ve dispatcher retry'si duplicate üretebilir. Adapter cancellation'ı başarıya çevirmez.

`Kafka:BootstrapServers`, `Kafka:Topic`, `Kafka:ClientId`, `Kafka:DeliveryTimeoutMilliseconds`, `Kafka:RequestTimeoutMilliseconds` ve `Kafka:MaximumRetries` options/configuration üzerinden gelir. Host:port listesi, Kafka topic karakter/249-byte sınırı, client ID, `1–300000 ms` timeout aralığı, request ≤ delivery ilişkisi ve `1–100` retry aralığı startup'ta doğrulanır. Repoda broker credential'ı yoktur. Checked-in compose ve bu checkpoint'in dar adapter'ı PLAINTEXT yerel/test sınırıdır; SASL/TLS ve production credential configuration bu adımda eklenmemiştir.

### Kafka consumer kabul sözleşmesi ve offset sınırı

`IReleaseRiskEventConsumer.Consume` yalnız bir `CancellationToken` alır ve yapılandırılmış topic'ten en fazla bir doğrulanmış `ConsumedReleaseRiskEvent` döndürür. Sonuç topic/partition/offset metadata'sını, ayrıştırılmış GUID key'ini, broker'dan gelen raw payload byte'larını ve typed V1 envelope'u birlikte taşır. Adapter başka topic'e fallback yapmaz ve topic oluşturmaz.

Kabul sırası şöyledir:

1. Record'un broker tarafından yapılandırılmış topic'ten geldiği doğrulanır.
2. Kafka key'inin standart tireli `D` biçiminde bir GUID olduğu doğrulanır.
3. Raw value UTF-8 JSON olarak mevcut `ReleaseRiskOutboxEnvelope` tipine ayrıştırılır.
4. Event type'ın tam olarak `releaseguard.release-risk-assessed`, `schemaVersion` değerinin `1`, snapshot identity/provider/kind alanlarının tutarlı olduğu doğrulanır.
5. Kafka key GUID'sinin envelope `eventId` değeriyle aynı olduğu doğrulanır.

Geçersiz record `ReleaseRiskEventContractException` üretir; adapter onu atlamaz, dönüştürmez veya yeni risk alanlarıyla tamamlamaz. Başarılı sonuçtaki `Payload`, broker'dan alınan byte dizisinin kopyasıdır. Producer/dispatcher entegrasyon testi bunun `ReleaseRiskOutboxEnvelope.SerializeToUtf8Bytes()` çıktısıyla byte düzeyinde aynı olduğunu kanıtlar.

`KafkaConsumer:BootstrapServers`, `KafkaConsumer:Topic`, `KafkaConsumer:GroupId`, `KafkaConsumer:ClientId`, `KafkaConsumer:ConsumeTimeoutMilliseconds` ve `KafkaConsumer:BrokerRequestTimeoutMilliseconds` configuration/options üzerinden gelir. Host:port, topic, group/client ID, `100–60000 ms` consume ve `1000–300000 ms` broker request sınırları startup'ta doğrulanır. Consumer topic'i `Kafka:Topic` ile ordinal olarak tam aynı olmak zorundadır; producer ve consumer'ın benzer görünen iki ayrı topic'te sessizce ayrışmasına izin verilmez. Group ID rastgele instance kimliği değildir: aynı mantıksal consumer workload'u için deploy'lar arasında kararlı, diğer workload'lardan ayrı bir değer olmalıdır. Repoya credential yazılmaz; bu yerel/test adapter'ı producer gibi PLAINTEXT sınırında kalır.

Adapter hem `EnableAutoCommit=false` hem `EnableAutoOffsetStore=false` kullanır. `Commit`, yalnız aynı adapter instance'ının son doğrulayarak döndürdüğü topic/partition/offset için çağrılabilir ve Kafka'ya `offset + 1` yazar. Başka instance'tan veya elle üretilmiş record commit edilmeye çalışılırsa broker çağrısından önce reddedilir. Synchronous broker commit başarılı dönmeden worker ilerlemez; hata çağırana taşınır ve uygulama katmanında internal retry yapılmaz. Commit çağrısı başladıktan sonra caller cancellation token'ı Confluent'ın synchronous isteğini kesemez; bekleyiş `BrokerRequestTimeoutMilliseconds` üzerinden bounded tutulur.

Record bounded süre içinde gelmezse `Consume` `null` döner. Bu sonuç broker'ın sağlıklı olduğuna dair health probe değildir: boş/yanlış topic ile erişilemeyen broker aynı çağrı penceresinde record üretmeyebilir. Çağıranın token'ı çağrıdan önce veya beklerken iptal edilirse cancellation başarıya ya da timeout'a çevrilmez, `OperationCanceledException` çağırana taşınır. Commit edilmeden dispose edilen record aynı group ID tarafından yeniden okunabilir.

### PostgreSQL inbox ve durable-accept-then-commit sınırı

V004 `release_risk_event_inbox` tablosunu oluşturur. `event_id` primary key ve `message_key = event_id` constraint'i aynı iş olayını idempotency anahtarı yapar. `topic + kafka_partition + kafka_offset` unique constraint'i tek broker konumunun iki ayrı inbox event'i gibi saklanmasını engeller. Event type, schema version, provider, kind ve envelope/top-level alan kümesi V1 sözleşmesine DB constraint'leriyle bağlanır. Inbox'ın outbox tablosuna foreign key'i yoktur; consumer sınırı başka bir producer deployment'ından gelen geçerli V1 record'u da kabul edebilir.

`payload bytea`, broker'dan gelen exact byte dizisini saklar; `envelope jsonb` ise sorgulanabilir semantik snapshot'tır. İlk insert `Accepted` döner. Aynı `eventId` ve byte düzeyinde aynı payload başka bir Kafka offset'inde tekrar gelirse yeni satır açılmaz ve `Duplicate` döner. Aynı `eventId` farklı raw payload ile gelirse `ReleaseRiskInboxConflictException` üretilir; kimlik çakışmasını normal duplicate gibi gösterip offset commit etmek bilinçli olarak yasaktır. Eşzamanlı iki instance primary key yarışını PostgreSQL `ON CONFLICT` ile koordine eder; loser committed payload'ı yeni statement snapshot'ında okuyup exact eşitliği doğrular.

`ReleaseRiskInboxProcessor` tek record'u sırasıyla tüketir, `InboxProcessor:PersistenceTimeoutMilliseconds` ile bounded PostgreSQL transaction'ına kabul eder, caller cancellation'ını tekrar kontrol eder ve ancak sonra explicit Kafka commit yapar. `InboxProcessor:Enabled` default olarak `false` olduğu için deploy açıkça etkinleştirmeden consumer oluşturulmaz veya record okunmaz. Processor enabled iken paralel record işleme yapmaz; partition ordering garantisi iddia etmese de bir instance'ın daha sonraki offset'i başarısız record'un üzerinden commit etmesini engeller.

DB hatası veya persistence timeout'unda transaction commit edilmez ya da sonucu belirsiz kalabilir; Kafka offset kesinlikle commit edilmez. DB commit başarılı olduktan sonra shutdown veya Kafka commit hatası olursa inbox satırı kalır ve offset yeniden teslim edilebilir. Restart/rebalance sonrası aynı payload `Duplicate` olur ve offset yeniden commit edilir. Commit gerçekte başarılı olduğu halde yanıt kaybolmuşsa broker zaten sonraki offset'ten devam eder; iki durumda da inbox kaydı doğrudur.

Malformed key/payload/type/version consumer kabulünde exception üretir. Processor bu hatayı yutmaz, offset'i ilerletmez, sonraki record'a geçmez ve exception'ı host'a taşıyan fail-stop davranışı gösterir. DB/commit/payload conflict hataları da aynı nedenle worker içinde retry/backoff döngüsüne çevrilmez. Bu checkpoint DLQ veya poison-record skip politikası tanımlamadığından operatör müdahalesi/restart öncesi record yerinde kalır.

### Inbox sonrası AI açıklama claim, retry, terminal sonuç ve DLQ sözleşmesi

V005 accepted V1 payload ile başarılı AI sonucu arasındaki ownership/retry alanlarını, V006 ise terminal sonucu aynı `release_risk_event_inbox` satırına ekler:

| Alan | Anlam |
| --- | --- |
| `explanation_attempt_count` | Atomic claim kazanıldığında artar; client çağrısından önce crash olsa da deneme görünür kalır. |
| `explanation_next_attempt_at` | Satırın yeniden claim edilebileceği en erken DB zamanı; retryable hata sonrası capped exponential backoff ile ileri taşınır. |
| `explanation_claimed_by` | Processor instance ID + batch claim GUID'sinden oluşan, her claim çağrısında benzersiz fencing token. |
| `explanation_claim_expires_at` | Token'ın DB saatine göre geçerlilik sınırı; expiry sonrası başka instance işi kurtarabilir. |
| `explanation_completed_at` / `explanation` | Yalnız aktif owner geçerli event-bound sonucu yazdığında birlikte dolar. |
| `explanation_failed_at` | Yalnız aktif owner terminal sonucu yazdığında ya da attempt limitindeki expired iş restart'ta sonlandırıldığında dolar. |
| `explanation_failure_code` | Programatik, küçük harf snake-case stabil terminal hata sınıfı; en fazla 64 ASCII karakter. |
| `explanation_failure_reason` | Exception/credential/response body sızdırmayan, boş olmayan ve en fazla 1024 byte operatör açıklaması. |

Bir satır yalnız üç durumdan birindedir: sonuçsuz pending/claimed, tamamlanmış açıklama veya terminal failure. V006 constraint'leri başarı ile terminal alanlarının birlikte dolmasını ve terminal satırda claim kalmasını reddeder. Başarılı açıklama için V005'in alan kümesi, event ID ve içerik constraint'leri aynen geçerlidir. `payload`, `envelope` ve dolayısıyla `riskAssessment.score`, `level`, `factors` snapshot'ı hiçbir sonuç geçişinde değişmez.

Claim işlemi kısa bir PostgreSQL transaction'ında önce due, sahipsiz/expired ve attempt sayısı yapılandırılmış limite ulaşmış satırları bounded `FOR UPDATE SKIP LOCKED` seçimiyle `attempt_limit_exhausted` terminal sonucuna taşır; ardından yalnız `explanation_attempt_count < MaximumAttempts` olan pending satırları claim eder. Bu sıra, son claim'den sonra process crash olduğunda işi yeniden HTTP'ye göndermeden kalıcı sonlandırır. Claim batch'i `1–100` ile sınırlıdır ve dönen satırlar batch içinde paralel işlenir. Index sırası `explanation_next_attempt_at, accepted_at, event_id` yalnız verimli/deterministik tarama içindir; iş olayı ordering veya latest-state garantisi değildir.

`IReleaseRiskExplanationClient.ExplainAsync` yalnız claim kazanıldıktan sonra inbox'taki typed V1 `envelope` ile çağrılır. Başarılı response'un `eventId` değeri claim event ID'sine eşit, summary ve tüm recommendations değerleri dolu olmalıdır. Completion, retry, terminal ve cancellation-release update'leri `event_id + claimed_by + attempt_count` fencing predicate'iyle çalışır; completion/retry/terminal için lease ayrıca DB saatine göre dolmamış olmalıdır. Daha yeni owner işi aldıktan sonra dönen stale owner mevcut sonucu değiştiremez.

Hata sınıfları şöyledir:

| Sinyal | Karar | Kalıcı kod |
| --- | --- | --- |
| Request `TimeoutException` | Retryable | `request_timeout` |
| Status taşımayan `HttpRequestException` | Retryable | `transport_error` |
| HTTP `408` | Retryable | `remote_timeout` |
| HTTP `429` | Retryable | `remote_throttled` |
| HTTP `5xx` | Retryable | `remote_server_error` |
| Beklenmeyen exception | Retryable, fakat bounded | `unexpected_error` |
| Response `eventId` conflict | Doğrudan terminal | `event_id_conflict` |
| Response contract ihlali | Doğrudan terminal | `response_contract_invalid` |
| Claim/request contract ihlali | Doğrudan terminal | `request_contract_invalid` |
| Yukarıdakiler dışındaki non-success HTTP durumu | Doğrudan terminal | `remote_client_error` |
| Attempt limitinde expired/sahipsiz claim | Doğrudan terminal | `attempt_limit_exhausted` |

Retryable hata ve kalan attempt varsa aktif owner `initialDelay × 2^(attempt-1)` gecikmesini hesaplayıp `MaximumRetryDelayMilliseconds` değerinde cap eder, `next_attempt_at` alanına DB saatiyle yazar ve claim'i bırakır. Aynı hata son attempt'te oluşursa kendi stabil koduyla terminalleşir ve reason configured limitin ulaşıldığını belirtir. Terminal sınıf kalan attempt'leri tüketmeden sonlanır. Beklenmeyen hataların doğrudan terminal sayılmaması geçici uygulama/altyapı belirsizliğine toparlanma fırsatı verir; bounded limit sonsuz döngüyü engeller. Jitter hâlâ yoktur.

HTTP beklerken veya completion başlamadan önce gözlenen caller/shutdown cancellation başarıya, retry'ya ya da terminal failure'a çevrilmez: in-flight HTTP token'ı iptal edilir, claim bağımsız bounded state-update token'ıyla hemen bırakılmaya çalışılır ve cancellation çağırana yayılır. Process aniden ölürse release çalışmayabilir; expiry recovery devreye girer. HTTP servisi ilk çağrıyı işlemiş fakat timeout/cancellation yüzünden response kaybolmuş olabilir; PostgreSQL ile HTTP arasında ortak transaction bulunmadığından provider çağrısı duplicate olabilir. Güvenli sınır, `eventId` başına tek değişmez kalıcı başarı veya terminal sonuçtur; provider maliyeti için exactly-once iddiası yoktur.

`MarkTerminalAsync` aktif claim'de terminal sonucu bir kez yazar. Aynı `eventId`, kod ve reason ile tekrar çağrı satırı değiştirmeden idempotent başarı döner; farklı terminal reason, tamamlanmış başarı veya kaybedilmiş ownership `false` döner. Kafka duplicate kabulü de mevcut attempt/başarı/terminal state'ini sıfırlamaz. Böylece restart, duplicate ve stale owner yarışları yeni satır veya çelişkili sonuç üretmez.

Operatör sorgusu `release_risk_ai_explanation_failed_work` görünümüdür. Görünüm yalnız `event_id`, attempt/failed zamanı, failure kod/nedeni, `accepted_at` ve immutable `envelope` alanlarını taşır; raw payload, claim token'ı veya mutasyon yüzeyi açmaz. `DISTINCT` tanımı görünümü PostgreSQL seviyesinde doğrudan update edilemez kılar. Store'daki `ReadFailedWorkAsync(limit)` aynı sözleşmeyi `1–100` arasında bounded okur. Migration rol/`GRANT` yönetmez; production'da operatör rolüne yalnız bu görünüm için `SELECT` verilmelidir. Bu checkpoint replay komutu, UI/API, retention, domain ordering veya latest-state eklemez.

### Outbox dispatcher claim, retry ve crash semantiği

V003 mevcut outbox tablosuna şu alanları ekler:

| Alan | Anlam |
| --- | --- |
| `published_at` | Yalnız Kafka acknowledgement sonrası yazılır; null ise yayın yaşam döngüsü tamamlanmamıştır. |
| `attempt_count` | Atomic claim kazanıldığında artar; process publish'e ulaşmadan ölse bile deneme görünür kalır. |
| `next_attempt_at` | Yeni claim'in en erken alınabileceği DB zamanı; publish hatası capped exponential backoff ile ileri taşır. |
| `claimed_by` | Dispatcher instance ID + batch claim GUID'sinden oluşan, her claim çağrısında benzersiz fencing token. |
| `claim_expires_at` | Token'ın geçerli olduğu DB zamanı; expiry sonrası başka instance aynı satırı yeniden claim edebilir. |

Claim tek SQL statement'ında eligible satırları `FOR UPDATE SKIP LOCKED` ile seçip token/expiry/attempt alanlarını update eder ve V1 envelope'u döndürür. İki instance yarışında row lock'u alan statement kazanır; diğeri aynı satırı bekleyip ikinci kez almak yerine skip eder. Pending index `next_attempt_at, created_at, event_id` taramasını destekler. Bu seçim sırası **iş olayı ordering garantisi değildir**; yalnız claim sorgusunun deterministik çalışma sırasıdır.

Bir batch claim edildikten sonra record'lar aynı singleton Kafka producer üzerinden bounded batch paralelliğinde yayımlanır; böylece son record'lar lease süresini sırada bekleyerek tüketmez. `published_at` ve retry update'leri yalnız aynı `claimed_by` token'ı hâlâ mevcut ve lease süresi dolmamışsa kabul edilir. Expired veya başka instance tarafından yenilenmiş token stale completion yazamaz. Etkin dispatcher için lease süresinin Kafka delivery timeout + bounded DB state-update timeout toplamından uzun olması startup'ta doğrulanır.

Publish exception'ında `initialDelay × 2^(attempt-1)` hesaplanır ve `MaximumRetryDelayMilliseconds` değerinde cap edilir; sonsuz sıkı döngü veya jitter yoktur. Failure state update'i başarısızsa claim olduğu gibi kalır ve lease expiry recovery sağlar. Kafka ack başarılı fakat `published_at` update'i başarısız/sonucu belirsizse aynı recovery yolu record'u yeniden yayımlayabilir. Bu duplicate olasılığı at-least-once davranışının merkezidir; Kafka key aynı GUID olsa da topic benzersizlik sağlamaz. V004 inbox consumer tarafında aynı GUID + exact payload tekrarını idempotent sonlandırır.

Dispatcher default olarak `Enabled=false` başlar; deploy açıkça etkinleştirmelidir. Disabled instance outbox'a dokunmaz. Shutdown token'ı yeni polling/claim'i durdurur ve in-flight producer task'larını iptal eder. Her iptal edilen claim bağımsız `StateUpdateTimeoutMilliseconds` token'ıyla hemen serbest bırakılmaya çalışılır; DB erişilemiyorsa lease süresi sonunda yeniden alınabilir. Broker cancellation sırasında record'u yine kabul etmiş olabileceğinden hızlı release de duplicate riskini ortadan kaldırmaz.

`OutboxDispatcher:BatchSize`, `PollIntervalMilliseconds`, `LeaseDurationMilliseconds`, `InitialRetryDelayMilliseconds`, `MaximumRetryDelayMilliseconds` ve `StateUpdateTimeoutMilliseconds` dar options sınırından gelir. Batch `1–100`, poll `100–60000 ms`, lease `5000–300000 ms`, state update `1000–30000 ms`, retry delay'leri `100–3600000 ms` aralıklarında doğrulanır. Bu checkpoint dead-letter/max-attempt sonlandırması eklemez; broker düzeldiğinde yayın niyetini kaybetmemek için retry kalıcı ve capped'tir.

### Neden HMAC-SHA256 ve sabit-zamanlı karşılaştırma?

GitHub'ın `X-Hub-Signature-256` sözleşmesi `sha256=<64 hex karakter>` biçimindedir. Sunucu aynı secret ve ham gövdeyle HMAC-SHA256 üretir. İki digest normal eşitlik operatörüyle değil `CryptographicOperations.FixedTimeEquals` ile karşılaştırılır; böylece eşleşen prefix uzunluğundan bilgi sızdıran timing saldırısı riski azaltılır.

Trade-off: yalnızca GitHub'ın SHA-256 imza şeması kabul edilir; eski `X-Hub-Signature`/SHA-1 başlığı bilinçli olarak desteklenmez.

### Secret neden options/configuration içinde?

Kod yalnızca `GitHubWebhook:Secret` yapılandırma anahtarını bilir; gerçek secret repoya yazılmaz. .NET configuration hiyerarşisi sayesinde yerelde `GitHubWebhook__Secret` ortam değişkeni, üretimde ise ortamın secret manager/configuration provider'ı kullanılabilir. Uygulama secret boşsa veya 32 karakterden kısaysa `ValidateOnStart` ile açılmaz; yanlış yapılandırmayla güvensiz çalışmak yerine fail-fast davranır.

Minimum uzunluk tahmin edilmesi kolay secret riskini azaltır. Trade-off: eski ve kısa bir secret doğrudan kullanılamaz; en az 32 karakterlik yüksek entropili yeni bir değer üretilmelidir. Ortam değişkeni pratik bir yerel geliştirme seçeneğidir; üretimde platformun secret manager'ı tercih edilmelidir.

PostgreSQL bağlantısı da yalnızca `PostgreSql:ConnectionString` yapılandırma anahtarından alınır; repoda gerçek bağlantı bilgisi veya parola yoktur. Boş, ayrıştırılamayan ya da `Host` / `Database` içermeyen değer `ValidateOnStart` ile uygulamayı durdurur. Bağlantı kurulamazsa veya şema beklenen sürümde değilse HTTP sunucusu trafik almadan startup başarısız olur. Bu, yanlış yapılandırılmış bir instance'ın bellek fallback'iyle sessizce idempotency kaybetmesini engeller.

### Migration ve startup stratejisi

V001 `github_webhook_deliveries` tablosunu, V002 `release_risk_outbox_messages` tablosunu ve accepted-only ilişki constraint'lerini, V003 dispatcher yaşam döngüsü kolon/constraint/index'lerini, V004 `release_risk_event_inbox` tablosunu, V005 inbox sonrası AI açıklama ownership/retry/başarı alanlarını, V006 ise terminal alan/constraint/index ve failed-work görünümünü oluşturur. Altı SQL dosyası da build sırasında assembly'ye gömülür; `release_guard_schema_migrations` uygulanan sürümleri kaydeder. Varsayılan `PostgreSql:ApplyMigrationsOnStartup=false` davranışı DDL çalıştırmaz; yalnızca migration sürümünün tam olarak uygulamanın beklediği V006 olduğunu, üç uygulama tablosunun gerekli kolonlarıyla ve failed-work görünümünün sözleşme alanlarıyla erişilebilirliğini doğrular. Böylece normal production runtime rolüne DDL yetkisi vermek zorunlu değildir.

Migration açıkça `true` yapıldığında uygulama transaction-scoped PostgreSQL advisory lock alır, migration metadata tablosunu oluşturur ve eksik migration'ları sürüm sırasıyla aynı transaction içinde uygular. Boş veritabanı V001→V002→V003→V004→V005→V006 yolundan geçer. V003 mevcut outbox satırlarını pending hale getirir; V004 mevcut delivery/outbox satırlarından inbox backfill etmez çünkü yalnız Kafka'da gerçekten tüketilmiş record kalıcı consumer kabulü sayılır. V005 mevcut V004 inbox satırlarını attempt `0`, due-now ve sonuçsuz pending açıklama işi haline getirir. V006 mevcut V005 pending veya başarılı satırları sonuçlarını değiştirmeden null terminal alanlarla yükseltir; yeni max-attempt davranışı ancak processor/store çalıştığında uygulanır. Lock, aynı deployment'ta birden fazla instance migration başlatırsa DDL yarışını seri hale getirir. Bu dar runner yalnızca ileri yönlü, bilinen migration'ları uygular; rollback/down migration veya kapsamlı bir migration framework'ü iddia etmez.

Production'da önerilen kullanım migration yetkili bağlantıyla kontrollü tek seferlik bir startup/iş olarak `ApplyMigrationsOnStartup=true` çalıştırmak, ardından runtime instance'larını daha dar yetkili bağlantıyla ve bayrak kapalı başlatmaktır. Migration'lar rol veya `GRANT` yönetmez; runtime rolünün delivery, outbox ve inbox için gerekli yetkileri platformun veritabanı yönetim sürecinde açıkça verilmelidir. Migration sırasında uygulama servis etmeye başlamaz. Migration hatası transaction'ı rollback eder ve startup'ı durdurur; operatör hatayı düzeltip aynı sürümü güvenle yeniden çalıştırabilir.

### HTTP durumları neden böyle?

| Durum | Yanıt | Gerekçe |
| --- | --- | --- |
| Desteklenen event/action (`opened` veya `synchronize`) ve sözleşme geçerli, GUID yeni | `202 Accepted` + `accepted` receipt | Teslimat, risk snapshot'ları ve tek V1 outbox envelope'u aynı DB transaction'ında commit edildi. |
| Teslimat geçerli fakat event/action desteklenmiyor, GUID yeni | `202 Accepted` + `ignored` receipt | Kalıcı ignored kabul commit edildi; göndericinin yeniden denemesine yol açılmaz. |
| GUID daha önce kaydedilmiş | `200 OK` + `duplicate` receipt | Tekrar güvenli biçimde sonlandırıldı; gönderici bunu hata sayıp yeniden denemez. |
| İmza başlığı eksik | `401 Unauthorized` | İstek gerekli kimlik doğrulama kanıtını taşımıyor. |
| İmza şeması/uzunluğu/hex biçimi bozuk | `400 Bad Request` | İstemci imza protokolüne uymayan bir değer gönderdi. |
| Biçimi doğru fakat digest yanlış | `401 Unauthorized` | Kimlik doğrulama başarısız; ayrıntılı karşılaştırma bilgisi açıklanmaz. |
| İmza geçerli fakat teslimat/olay başlığı eksik, GUID bozuk, JSON geçersiz veya desteklenen payload eksik | `400 Bad Request` | Güvenlik kontrolünü geçen istek gerekli sözleşmeye dönüştürülemedi. |

`X-Hub-Signature-256` tamamen eksikse `401`; imza başlığı var fakat biçimi bozuksa `400` davranışı korunur. İlk desteklenen teslimattaki `202`, doğrulanmış sözleşme, risk snapshot'ları ve outbox handoff'unun PostgreSQL'e commit edildiğini bildirir. DB bağlantı/transaction hatası framework tarafından `5xx` olarak döner; teslimat kaydedilmiş gibi `202` üretilmez. Outbox satırının varlığı harici event'in yayımlandığı anlamına gelmez.

### Neden monorepo?

ReleaseGuard'ın .NET servisleri, Python AI servisi, olay sözleşmeleri ve ilerideki dashboard'u aynı ürünün parçalarıdır. Erken aşamada tek depo kullanmak:

- bir olay sözleşmesi değiştiğinde üretici, tüketici ve testleri tek değişiklikte güncellemeyi,
- tek bir yerel geliştirme ve CI giriş noktası sunmayı,
- mimari kararları ve sürüm geçmişini birlikte tutmayı

kolaylaştırır.

Trade-off şudur: depo büyüdükçe tüm projeleri her değişiklikte derlemek pahalılaşabilir ve servis sınırları bulanıklaşabilir. Bunu net klasör/proje sınırları, bağımlılık kuralları ve ileride path-filtered CI ile yöneteceğiz. Takımların yayın döngüleri gerçekten bağımsızlaşırsa bazı bileşenleri ayrı depolara taşımak yeniden değerlendirilebilir.

### Neden şimdilik .NET 8?

Hedef teknoloji .NET 10'dur; ancak incelenen makinede yalnızca .NET SDK `8.0.416` ve .NET runtime `8.0.22` kurulu. Çalışmadığı doğrulanamayan `net10.0` dosyaları üretmek yerine ilk checkpoint `net8.0` ile derlenebilir tutuldu. `global.json` kullanılan SDK'yı sabitler. .NET 10 SDK kurulduğunda hedef framework ayrı bir yükseltme adımında değiştirilecek ve tüm testler yeniden çalıştırılacaktır.

### Kafka neden webhook transaction'ı içinde çağrılmıyor?

Webhook transaction'ı içinde Kafka çağırmak PostgreSQL commit'i ile broker acknowledgement'ını tek atomik işlem gibi gösteremez; broker hatası transaction'ı gereksiz yere açık tutar, DB commit sonrası process crash'i ise yine belirsiz bir pencere bırakır. Bu nedenle webhook davranışı değişmez: önce delivery ve outbox niyeti birlikte commit edilir, bağımsız background dispatcher daha sonra claim edip yayımlar.

Dispatcher'ın PostgreSQL state update'i Kafka ile ortak transaction değildir. Kafka ack sonrası DB update başarısız olursa expiry/retry duplicate üretebilir; DB `published_at` yazıldıktan sonra broker record'unu geri çekme ihtiyacı yoktur çünkü producer ancak acknowledgement sonrası döner. Consumer inbox transaction'ı da Kafka offset commit'iyle ortak transaction değildir; DB-first sıra ve `eventId` uniqueness bu ikinci belirsiz pencereyi at-least-once güvenli yapar. AI HTTP çağrısı da PostgreSQL ile ortak transaction'a katılmaz: processor önce DB claim alır, sonra HTTP çağrısı yapar ve sonucu aktif fencing token ile yazar. HTTP başarıdan sonra completion update'i kaybolursa expiry/retry aynı `eventId` için duplicate servis çağrısı üretebilir; tek kalıcı sonuç ownership predicate'iyle korunur.

### Bağımsız AI açıklama sözleşmesi

`POST /v1/release-risk-explanations` request body olarak Kafka value'suyla aynı mevcut V1 envelope'u alır. Pydantic modelleri bilinmeyen alanları yasaklar; `eventType = releaseguard.release-risk-assessed`, `schemaVersion = 1`, `sourceProvider = github`, desteklenen kind değerleri ve `eventId = riskInput.sourceDeliveryId` tutarlılığı zorunludur. Top-level provider/kind de `riskInput` snapshot'ıyla eşleşmelidir. HTTP sınırında ayrı bir Kafka key alanı yoktur; event/key kimliği V1'de zaten delivery GUID olan `eventId` ile `sourceDeliveryId` eşitliği üzerinden korunur.

Başarılı yanıt yalnız aşağıdaki sözleşmedir:

```json
{
  "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "summary": "The recorded risk ...",
  "recommendations": ["Review primary_target_branch: ..."]
}
```

`eventId` provider yanıtından alınmaz; doğrulanmış istekten servis tarafından bağlanır. Provider'ın dar yanıtı yalnız `summary` ve en az bir `recommendations` öğesi taşıyabilir. Böylece provider yeni score, level, ordering, latest-state veya deployment kararı ekleyemez. Mevcut `riskAssessment.score`, `level` ve `factors` değerleri provider request'ine aynen aktarılır; servis deterministik değerlendiriciyi yeniden çalıştırmaz.

`fake` provider yalnız local/test içindir. Mevcut level/score ile factor code/points/reason değerlerinden tamamen deterministik İngilizce metin üretir; harici modele çağrı yapmaz ve AI kalitesi iddiası taşımaz. `http-json` adapter'ı ise yapılandırılan endpoint'e bearer credential ile şu dar gövdeyi gönderir:

```json
{
  "model": "configured-model-name",
  "envelope": { "eventType": "releaseguard.release-risk-assessed" }
}
```

Buradaki `envelope` kısaltılmadan doğrulanmış V1 nesnesinin tamamıdır. Provider'ın `200` yanıtı tam olarak `{"summary":"...","recommendations":["..."]}` biçiminde olmalıdır; ek alan, eksik alan veya geçersiz JSON provider hatasıdır. Adapter provider'a özgü SDK veya prompt sözleşmesi uydurmaz; gerçek model gateway'i bu basit HTTP protokolünü sunmalıdır.

Tüm provider türleri endpoint seviyesinde `RELEASEGUARD_AI_TIMEOUT_SECONDS` ile bound edilir. HTTP transport timeout'u veya genel provider süresinin aşılması `504 Gateway Timeout`; bağlantı, non-2xx veya response-contract hatası ayrıntı sızdırmayan `502 Bad Gateway` üretir. İstek iptali başarıya veya 5xx'e çevrilmez; çalışan provider coroutine'i iptal edilir ve cancellation çağırana yayılır. Servis internal retry yapmaz; retry/idempotency kararı .NET inbox sonrası processor'da `eventId` ve kalıcı claim state'i üzerinden verilir.

### .NET AI açıklama HTTP client sözleşmesi

`IReleaseRiskExplanationClient.ExplainAsync`, yalnız mevcut typed `ReleaseRiskOutboxEnvelope` ile caller `CancellationToken` değerini alır. HTTP adapter önce envelope'un mevcut V1 identity/provider/kind tutarlılığını doğrular, ardından `SerializeToUtf8Bytes()` çıktısını değiştirmeden `application/json; charset=utf-8` gövdesi olarak yapılandırılmış base URL altındaki `v1/release-risk-explanations` yoluna gönderir. Ayrı bir request DTO'su veya AI'ya özgü score modeli yoktur; risk input, score, level ve factor snapshot'ları Python sınırına mevcut halleriyle ulaşır.

Yanıt JSON'u tam olarak `eventId`, `summary` ve `recommendations` alanlarıyla sınırlandırılır. Geçersiz JSON, ek/eksik alan veya boş içerik `ReleaseRiskExplanationContractException`; request ile response kimliği farklıysa bunun özel alt türü `ReleaseRiskExplanationEventIdConflictException` olur. Non-2xx `HttpRequestException` ve status code ile taşınır. Bu hataların hiçbiri başarıya çevrilmez.

Adapter request ve response-body okumasının tamamını `AiExplanationClient:RequestTimeoutMilliseconds` ile bound eder. Yapılandırılmış deadline aşılırsa `TimeoutException`, caller token iptal edilirse `OperationCanceledException` yayılır; caller cancellation timeout gibi yeniden sınıflandırılmaz. In-flight timeout/cancellation sırasında Python isteği işlemiş olabilir. Client internal retry yapmaz; kalıcı processor belirsiz sonucu aynı `eventId` için retry/lease kurallarıyla ele alır.

Client DI'a typed `HttpClient` olarak kaydedilir ve yalnız `ReleaseRiskExplanationProcessor` tarafından, kalıcı claim sahipliği altında çağrılır. Kafka consumer/inbox processor HTTP beklemez; offset commit sırası durable inbox kabulünde sona ermeye devam eder. Python endpoint'i kendi başına authentication header istemediği için .NET client credential taşımaz. İleride gateway authentication gerekirse token/header kod veya appsettings içine yazılmamalı; configuration/secret provider üzerinden alınmalıdır.

### AI servis yapılandırması

| Environment değişkeni | Kural |
| --- | --- |
| `RELEASEGUARD_AI_PROVIDER` | Zorunlu; yalnız `fake` veya `http-json`. |
| `RELEASEGUARD_AI_MODEL` | Zorunlu, boş olmayan model/deployment adı. |
| `RELEASEGUARD_AI_TIMEOUT_SECONDS` | Zorunlu, sonlu `0.1–60` saniye. |
| `RELEASEGUARD_AI_PROVIDER_ENDPOINT` | `http-json` için zorunlu; HTTPS olmalı. Yalnız local testte loopback HTTP kabul edilir. URL credential veya fragment taşıyamaz. |
| `RELEASEGUARD_AI_PROVIDER_API_KEY` | `http-json` için zorunlu; yalnız environment/secret provider üzerinden verilir. |

Eksik/geçersiz yapılandırma `releaseguard_ai.main` yüklenirken açıkça startup hatası üretir; sessiz fake fallback yoktur. Repoda credential veya çalışan provider default'u bulunmaz. `/health` yalnız process/API sağlığını bildirir ve uzak provider'ı çağırmaz; model sağlayıcısının erişilebilirlik kanıtı değildir.

### .NET AI client yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `AiExplanationClient:BaseUrl` | Zorunlu absolute HTTP/HTTPS base URL. Credential, query veya fragment taşıyamaz. Local örnek: `http://127.0.0.1:8090`. |
| `AiExplanationClient:RequestTimeoutMilliseconds` | `100–60000` aralığında bounded tam request süresi; default `5000`. |

.NET environment değişkeni eşlemesi için sırasıyla `AiExplanationClient__BaseUrl` ve `AiExplanationClient__RequestTimeoutMilliseconds` kullanılır. Base URL bilinçli olarak boş default'a sahiptir; yanlış ortamın bilinmeyen bir endpoint'e sessizce bağlanması yerine `ValidateOnStart` uygulamayı durdurur.

### .NET AI açıklama processor yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `AiExplanationProcessor:Enabled` | Default `false`; `true` olmadan inbox satırı claim edilmez ve AI servisi çağrılmaz. |
| `AiExplanationProcessor:BatchSize` | Her poll'da claim edilecek azami satır; `1–100`, default `10`. |
| `AiExplanationProcessor:PollIntervalMilliseconds` | İş bulunmadığında bekleme; `100–60000`, default `1000`. |
| `AiExplanationProcessor:LeaseDurationMilliseconds` | Claim geçerlilik süresi; `1000–300000`, default `30000`. Enabled iken client request timeout + state-update timeout toplamından büyük olmalıdır. |
| `AiExplanationProcessor:InitialRetryDelayMilliseconds` | İlk hata gecikmesi; `100–3600000`, default `1000`. |
| `AiExplanationProcessor:MaximumRetryDelayMilliseconds` | Exponential backoff cap'i; `100–3600000`, default `60000` ve initial değerden küçük olamaz. |
| `AiExplanationProcessor:MaximumAttempts` | Atomic claim sayısı sınırı; `1–100`, default `5`. Son retryable hata veya expired claim bu sınırda terminalleşir. |
| `AiExplanationProcessor:StateUpdateTimeoutMilliseconds` | Complete/fail/release DB update sınırı; `100–30000`, default `5000` ve lease'ten küçük olmalıdır. |

Environment değişkenlerinde `:` yerine `__` kullanılır. Processor enabled değilse lifecycle satırları durable pending kalır; bu güvenli default bir başarı veya drop sayılmaz. Enabled instance batch içinde bounded paralellik kullanır. `MaximumAttempts`, aynı event için provider'ın kesin çağrı sayısı değildir: HTTP başlamadan önce crash olan claim de attempt sayılır, timeout sonrası uzak servis çağrıyı işlemiş olabilir. Bu muhafazakâr sınır sonsuz maliyeti engeller; operatör failed-work reason ve envelope üzerinden sonucu inceleyebilir. Jitter ve manual replay yoktur.

## Yerel ortam bulguları

İlk incelemede aşağıdaki araçlar doğrulandı:

| Araç | Sürüm / durum |
| --- | --- |
| .NET SDK | 8.0.416; .NET 10 kurulu değil |
| Docker Engine | 29.0.1, Linux/aarch64 daemon erişilebilir |
| Docker Compose | v2.40.3-desktop.1 |
| Python | 3.9.6; pip 21.2.4 |
| Node.js / npm | 22.21.1 / 10.9.4 |
| Git | 2.50.1 |

Workspace'teki mevcut `ActivityIngestionService`, `docker-compose.yaml`, `output/` ve `tmp/` içerikleri bu projeden ayrı kabul edildi ve değiştirilmedi.

## Dizin yapısı

```text
ReleaseGuardAI/
├── compose.kafka.yml
├── contracts/
│   └── release-risk-assessed.v1.example.json
├── Directory.Build.props
├── global.json
├── ReleaseGuard.sln
├── scripts/
│   └── test-dotnet-python-contract.sh
├── src/
│   ├── ReleaseGuard.AiExplanation.Api/
│   │   ├── pyproject.toml
│   │   ├── releaseguard_ai/
│   │   │   ├── app.py
│   │   │   ├── contracts.py
│   │   │   ├── main.py
│   │   │   ├── providers.py
│   │   │   └── settings.py
│   │   └── tests/
│   │       ├── conftest.py
│   │       ├── test_api.py
│   │       ├── test_contracts.py
│   │       ├── test_providers.py
│   │       └── test_settings.py
│   └── ReleaseGuard.WebhookIngestion.Api/
│       ├── AiExplanationClientOptions.cs
│       ├── AiExplanationFailureClassifier.cs
│       ├── AiExplanationProcessorOptions.cs
│       ├── Database/Migrations/
│       │   ├── V001__create_github_webhook_deliveries.sql
│       │   ├── V002__create_release_risk_outbox.sql
│       │   ├── V003__add_release_risk_outbox_dispatch_lifecycle.sql
│       │   ├── V004__create_release_risk_event_inbox.sql
│       │   ├── V005__add_release_risk_ai_explanation_lifecycle.sql
│       │   └── V006__add_release_risk_ai_explanation_terminal_lifecycle.sql
│       ├── GitHubWebhookDeliveryStore.cs
│       ├── GitHubWebhookEndpoint.cs
│       ├── GitHubWebhookOptions.cs
│       ├── GitHubWebhookReceipt.cs
│       ├── GitHubRiskInputMapper.cs
│       ├── GitHubWebhookSignatureValidator.cs
│       ├── KafkaConsumerOptions.cs
│       ├── KafkaProducerOptions.cs
│       ├── KafkaReleaseRiskEventConsumer.cs
│       ├── KafkaReleaseRiskEventProducer.cs
│       ├── OutboxDispatcherOptions.cs
│       ├── PostgreSqlOptions.cs
│       ├── PostgreSqlSchemaInitializer.cs
│       ├── ReleaseRiskAssessment.cs
│       ├── ReleaseRiskEvaluator.cs
│       ├── ReleaseRiskInput.cs
│       ├── ReleaseRiskInboxProcessor.cs
│       ├── ReleaseRiskInboxProcessorOptions.cs
│       ├── ReleaseRiskInboxStore.cs
│       ├── ReleaseRiskExplanationClient.cs
│       ├── ReleaseRiskExplanationProcessor.cs
│       ├── ReleaseRiskExplanationStore.cs
│       ├── ReleaseRiskOutboxDispatcher.cs
│       ├── ReleaseRiskOutboxEnvelope.cs
│       ├── ReleaseRiskOutboxStore.cs
│       └── VerifiedGitHubWebhook.cs
└── tests/
    └── ReleaseGuard.WebhookIngestion.Api.Tests/
        ├── AiExplanationClientOptionsTests.cs
        ├── AiExplanationFailureClassifierTests.cs
        ├── AiExplanationProcessorOptionsTests.cs
        ├── GitHubWebhookEndpointTests.cs
        ├── HealthEndpointTests.cs
        ├── HttpReleaseRiskExplanationClientTests.cs
        ├── KafkaIntegrationFixture.cs
        ├── KafkaConsumerOptionsTests.cs
        ├── KafkaProducerOptionsTests.cs
        ├── KafkaReleaseRiskEventConsumerIntegrationTests.cs
        ├── KafkaReleaseRiskEventProducerIntegrationTests.cs
        ├── OutboxDispatcherOptionsTests.cs
        ├── PostgreSqlAiExplanationProcessorIntegrationTests.cs
        ├── PostgreSqlGitHubWebhookIntegrationTests.cs
        ├── PostgreSqlInboxProcessorIntegrationTests.cs
        ├── PostgreSqlIntegrationFixture.cs
        ├── PostgreSqlOutboxDispatcherIntegrationTests.cs
        ├── PostgreSqlTestApplicationFactory.cs
        ├── PythonAiExplanationContractIntegrationTests.cs
        ├── ReleaseRiskEvaluatorTests.cs
        ├── ReleaseRiskExplanationProcessorTests.cs
        ├── ReleaseRiskInboxProcessorOptionsTests.cs
        ├── ReleaseRiskInboxProcessorTests.cs
        ├── ReleaseRiskOutboxDispatcherTests.cs
        ├── ReleaseRiskOutboxEnvelopeTests.cs
        └── TestApplicationFactory.cs
```

Dashboard ve ek production altyapı klasörleri ihtiyaç doğduğu checkpoint'lerde oluşturulacaktır; boş yer tutucu klasörler eklenmemiştir.

## Tekrarlanabilir komutlar

Komutları bu README'nin bulunduğu `ReleaseGuard` klasöründe çalıştırın:

```bash
dotnet format ReleaseGuard.sln
dotnet restore ReleaseGuard.sln --disable-build-servers -m:1
dotnet build ReleaseGuard.sln --no-restore --disable-build-servers -m:1
dotnet test ReleaseGuard.sln --no-build --disable-build-servers -m:1
```

`--disable-build-servers -m:1`, kısıtlı ortamlarda MSBuild sunucularını ve paralel düğümleri kapatır. Normal bir yerel terminalde sade komutlar da kullanılabilir:

```bash
dotnet restore ReleaseGuard.sln
dotnet build ReleaseGuard.sln --no-restore
dotnet test ReleaseGuard.sln --no-build
```

Python 3.9+ AI servisini ayrı sanal ortamda kurup doğrulamak için:

```bash
cd src/ReleaseGuard.AiExplanation.Api
python3 -m venv .venv
.venv/bin/python -m pip install --upgrade pip==25.2
.venv/bin/python -m pip install -e '.[test]'
.venv/bin/ruff check .
.venv/bin/ruff format --check .
PYTHONPYCACHEPREFIX=/tmp/releaseguard-pycache \
  .venv/bin/python -m compileall -q releaseguard_ai tests
.venv/bin/python -m pytest -q
```

Bağımlılıkların doğrudan sürümleri `pyproject.toml` içinde sabittir. `.venv`, Python cache'leri ve editable-install metadata'sı git tarafından yok sayılır.

Python sanal ortamı kurulduktan sonra gerçek local Uvicorn process'iyle tek .NET→Python V1 contract testini depo kökünden çalıştırmak için:

```bash
scripts/test-dotnet-python-contract.sh
```

Script yalnız ilgili xUnit testini seçer. Test boş bir loopback portu ayırır, `.venv/bin/python -m uvicorn` process'ini `fake` provider ile başlatır, production .NET adapter'ıyla ortak checked-in V1 fixture'ı endpoint'e gönderir ve process'i sonunda kapatır. `RELEASEGUARD_AI_PYTHON` ile farklı bir Python executable seçilebilir; test dış ağa veya ücretli provider'a başvurmaz.

Local/test deterministic provider ile servisi çalıştırmak için aynı klasörde:

```bash
export RELEASEGUARD_AI_PROVIDER='fake'
export RELEASEGUARD_AI_MODEL='deterministic-v1'
export RELEASEGUARD_AI_TIMEOUT_SECONDS='5'
.venv/bin/uvicorn releaseguard_ai.main:app \
  --host 127.0.0.1 --port 8090
```

Bağımsız .NET client yapılandırmasının aynı local servisi göstermesi için webhook host ortamında:

```bash
export AiExplanationClient__BaseUrl='http://127.0.0.1:8090'
export AiExplanationClient__RequestTimeoutMilliseconds='5000'
```

Bu ayarlar client transport'unu hazırlar. AI açıklama processor ayrıca etkinleştirilene kadar HTTP çağrısı yapılmaz; Kafka consumer/inbox kabul worker'ı AI yanıtını beklemez.

Başka bir terminalden `curl http://127.0.0.1:8090/health` çağrısı `{"status":"ok","service":"ai-explanation"}` döndürmelidir. Açıklama endpoint'ine request body olarak ortak fixture gönderilebilir. Aşağıdaki komut AI servis klasöründen çalıştırılır:

```bash
curl --fail-with-body \
  -H 'Content-Type: application/json' \
  --data-binary @../../contracts/release-risk-assessed.v1.example.json \
  http://127.0.0.1:8090/v1/release-risk-explanations
```

`http-json` kullanırken provider, model, HTTPS endpoint, timeout ve API key environment üzerinden verilir; örneklerde gerçek credential tutulmaz:

```bash
export RELEASEGUARD_AI_PROVIDER='http-json'
export RELEASEGUARD_AI_MODEL='configured-model-name'
export RELEASEGUARD_AI_PROVIDER_ENDPOINT='https://model-gateway.example/v1/explain'
export RELEASEGUARD_AI_PROVIDER_API_KEY='<secret-manager-value>'
export RELEASEGUARD_AI_TIMEOUT_SECONDS='10'
```

Tüm test paketi Testcontainers ile geçici `postgres:16-alpine` ve Kafka-compatible `docker.redpanda.com/redpandadata/redpanda:v26.1.14` container'ları başlatır. Docker Engine çalışıyor olmalıdır; testler Docker yokken sessizce skip edilmez. PostgreSQL fixture'ı rastgele host portu ve test başına izole veritabanı kullanır. Kafka fixture'ı rastgele uygun host portunda gerçek broker açar ve test başına benzersiz, açıkça oluşturulmuş topic kullanır. Consumer testleri gerçek group coordination ve offset davranışını kullanır; mock broker ile commit semantiği taklit edilmez.

Yalnızca gerçek PostgreSQL entegrasyon testlerini çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~PostgreSqlGitHubWebhookIntegrationTests
```

Yalnız Kafka options ve gerçek broker testlerini çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~Kafka
```

Yalnız dispatcher options/unit testleriyle gerçek PostgreSQL+Kafka yaşam döngüsü testlerini çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~OutboxDispatcher
```

Yalnız inbox processor options/unit testleriyle gerçek durable-accept-then-commit senaryolarını çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~InboxProcessor
```

Yalnız AI açıklama processor options/unit testleriyle gerçek PostgreSQL ownership/retry/terminal senaryolarını çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~ExplanationProcessor
```

### Yerel Kafka-compatible broker

Checked-in compose dosyası Linux/amd64 ve Linux/arm64 imajı bulunan, Kafka protokolüyle uyumlu tek Redpanda broker'ı `localhost:19092` üzerinde başlatır. Veriler container ile birlikte silinebilir yerel/test verisidir:

```bash
docker compose -f compose.kafka.yml up -d --wait
docker compose -f compose.kafka.yml exec redpanda \
  rpk topic create releaseguard.release-risk-assessed \
  --if-not-exists --partitions 1 --replicas 1 \
  -X brokers=localhost:19092
```

Topic'i doğrulamak ve işi bitirince broker'ı kaldırmak için:

```bash
docker compose -f compose.kafka.yml exec redpanda \
  rpk topic list -X brokers=localhost:19092
docker compose -f compose.kafka.yml down -v
```

Local broker PLAINTEXT'tir ve production güvenlik modeli değildir. Topic auto-create producer tarafında kapalı olduğundan topic deployment/provisioning adımında açıkça oluşturulmalıdır.

### Yerel PostgreSQL ve uygulama

Yerel bir PostgreSQL 16 örneği başlatın. Aşağıdaki parola yalnızca silinebilir yerel container örneğidir; production bilgisi değildir:

```bash
docker run --name releaseguard-postgres --rm \
  -e POSTGRES_DB=releaseguard \
  -e POSTGRES_PASSWORD=local-only-password \
  -p 5432:5432 \
  postgres:16-alpine
```

Başka bir terminalde en az 32 karakterlik webhook secret'ını, PostgreSQL bağlantısını, Kafka producer/consumer ayarlarını ve ilk çalıştırma için migration bayrağını yapılandırın. Aşağıdaki değerler yalnızca yerel örnektir; gerçek ortamda bağlantı bilgisi ile secret'i platformun secret manager/configuration provider'ından verin:

```bash
export GitHubWebhook__Secret='replace-with-a-random-secret-of-at-least-32-characters'
export PostgreSql__ConnectionString='Host=localhost;Port=5432;Database=releaseguard;Username=postgres;Password=local-only-password'
export PostgreSql__ApplyMigrationsOnStartup=true
export Kafka__BootstrapServers='localhost:19092'
export Kafka__Topic='releaseguard.release-risk-assessed'
export Kafka__ClientId='releaseguard-webhook-ingestion-local'
export Kafka__DeliveryTimeoutMilliseconds=10000
export Kafka__RequestTimeoutMilliseconds=5000
export Kafka__MaximumRetries=3
export KafkaConsumer__BootstrapServers='localhost:19092'
export KafkaConsumer__Topic='releaseguard.release-risk-assessed'
export KafkaConsumer__GroupId='releaseguard-release-risk-local'
export KafkaConsumer__ClientId='releaseguard-release-risk-consumer-local'
export KafkaConsumer__ConsumeTimeoutMilliseconds=5000
export KafkaConsumer__BrokerRequestTimeoutMilliseconds=5000
export OutboxDispatcher__Enabled=true
export OutboxDispatcher__BatchSize=10
export OutboxDispatcher__PollIntervalMilliseconds=1000
export OutboxDispatcher__LeaseDurationMilliseconds=30000
export OutboxDispatcher__InitialRetryDelayMilliseconds=1000
export OutboxDispatcher__MaximumRetryDelayMilliseconds=60000
export OutboxDispatcher__StateUpdateTimeoutMilliseconds=5000
export InboxProcessor__Enabled=true
export InboxProcessor__PersistenceTimeoutMilliseconds=5000
export AiExplanationClient__BaseUrl='http://127.0.0.1:8090'
export AiExplanationClient__RequestTimeoutMilliseconds=5000
export AiExplanationProcessor__Enabled=true
export AiExplanationProcessor__BatchSize=10
export AiExplanationProcessor__PollIntervalMilliseconds=1000
export AiExplanationProcessor__LeaseDurationMilliseconds=30000
export AiExplanationProcessor__InitialRetryDelayMilliseconds=1000
export AiExplanationProcessor__MaximumRetryDelayMilliseconds=60000
export AiExplanationProcessor__MaximumAttempts=5
export AiExplanationProcessor__StateUpdateTimeoutMilliseconds=5000
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
```

V001, V002, V003, V004, V005 ve V006 uygulandıktan sonra normal startup doğrulama modunu kullanın:

```bash
export PostgreSql__ApplyMigrationsOnStartup=false
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
```

Migration bayrağı hiç verilmezse `false` kabul edilir. V006'ya ulaşmamış veritabanında false ile açılış bilinçli olarak başarısızdır; şema kendiliğinden veya bellek fallback'iyle oluşturulmaz. Kafka producer/consumer bootstrap servers, aynı topic, consumer group ID, bounded consume/broker request timeout'u ile dispatcher/inbox processor sınırları eksik veya geçersizse uygulama options validation ile startup'ta durur. `OutboxDispatcher__Enabled`, `InboxProcessor__Enabled` ve `AiExplanationProcessor__Enabled` verilmezse güvenli default `false` olur; pending outbox yayımlanmaz, consumer client oluşturulup record okunmaz ve accepted inbox satırları AI için claim edilmez. Dispatcher publish hatalarını kalıcı capped backoff'a dönüştürür. AI processor retryable hataları configured attempt sınırına kadar backoff ile dener; terminal sınıfları ve son retryable denemeyi kalıcı failed state'e taşır. Inbox processor etkin olduğunda ilk güvenli olmayan DB/contract/commit hatası worker'ı durdurur; Kafka offset commit'i AI çağrısını beklemez.

Başka bir terminalden sağlık kontrolü:

```bash
curl http://localhost:5080/health
```

Beklenen sağlık yanıtı:

```json
{"status":"ok","service":"webhook-ingestion"}
```

GitHub webhook ayarında payload URL'sini `/webhooks/github`, content type'ı `application/json` ve secret'i uygulamaya verilen değerle aynı ayarlayın. GitHub her teslimatta gerekli `X-Hub-Signature-256`, `X-GitHub-Delivery` ve `X-GitHub-Event` başlıklarını gönderir. Geçerli bir `pull_request` / `synchronize` teslimatının örnek yanıtı şöyledir:

```json
{
  "deliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "eventName": "pull_request",
  "status": "accepted",
  "riskInput": {
    "sourceDeliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
    "sourceProvider": "github",
    "kind": "change_updated",
    "repository": "acme/ReleaseGuard",
    "changeNumber": 42,
    "title": "Protect production releases",
    "author": "octocat",
    "baseBranch": "main",
    "headBranch": "feature/release-guard",
    "isDraft": false,
    "changedFiles": 20,
    "additions": 1000,
    "deletions": 5
  },
  "riskAssessment": {
    "score": 100,
    "level": "high",
    "factors": [
      {
        "code": "broad_change",
        "points": 30,
        "reason": "20 changed files meets the broad-change threshold of 20 files."
      },
      {
        "code": "high_change_churn",
        "points": 50,
        "reason": "1,005 changed lines meets the high-churn threshold of 1,000 lines."
      },
      {
        "code": "primary_target_branch",
        "points": 20,
        "reason": "The change targets the conventional primary branch 'main'."
      }
    ]
  }
}
```

`opened` için aynı yanıt şekli korunur ve `riskInput.kind` değeri `change_opened` olur. Aynı GUID tekrar gelirse process veya instance fark etmeksizin `status` değeri `duplicate` olur. Desteklenmeyen event/action'ın ilk teslimatında `status` değeri `ignored`, aynı GUID tekrarlandığında `duplicate` olur; iki yanıtta da `riskInput` ve `riskAssessment` null döner.

Outbox'ı salt-okunur incelemek için örnek sorgu:

```sql
SELECT
    event_id,
    event_type,
    schema_version,
    source_provider,
    event_kind,
    envelope,
    created_at,
    published_at,
    attempt_count,
    next_attempt_at,
    claimed_by,
    claim_expires_at
FROM release_risk_outbox_messages
ORDER BY created_at, event_id;
```

`published_at IS NOT NULL`, producer'ın Kafka'dan `Persisted` acknowledgement aldığını ve bu sonucu aktif claim ile PostgreSQL'e kaydettiğini gösterir; consumer'ın record'u okuduğunu veya işlediğini göstermez. `published_at IS NULL` ve `claimed_by IS NOT NULL` aktif ya da süresi dolmayı bekleyen bir lease'tir. Null claim ile gelecekteki `next_attempt_at` publish hatası backoff'unu; null claim ve geçmiş/şimdiki `next_attempt_at` yeniden claim edilebilir pending satırı gösterir.

Kalıcı consumer kabulünü salt-okunur incelemek için:

```sql
SELECT
    event_id,
    message_key,
    topic,
    kafka_partition,
    kafka_offset,
    event_type,
    schema_version,
    source_provider,
    event_kind,
    payload,
    envelope,
    accepted_at,
    explanation_attempt_count,
    explanation_next_attempt_at,
    explanation_claimed_by,
    explanation_claim_expires_at,
    explanation_completed_at,
    explanation,
    explanation_failed_at,
    explanation_failure_code,
    explanation_failure_reason
FROM release_risk_event_inbox
ORDER BY accepted_at, event_id;
```

Inbox satırının varlığı V1 Kafka record'unun PostgreSQL'e durable kabul edildiğini gösterir; Kafka offset commit'inin kesin olarak başarılı olduğunu tek başına kanıtlamaz. `payload` bytea exact broker value'dur; okunabilir risk snapshot'ı için `envelope`, taşıma kanıtı için `payload` kullanılmalıdır. `explanation_completed_at IS NOT NULL` ile non-null `explanation` başarıyı, `explanation_failed_at IS NOT NULL` ile kod/neden terminal failure'ı gösterir; bu iki durum DB constraint'i gereği birlikte oluşamaz. İki sonuç da null iken non-null claim aktif veya expiry bekleyen işi; null claim + gelecekteki next-attempt retry backoff'unu; null claim + due next-attempt yeniden alınabilir pending işi gösterir. Bu kolonlar ordering/latest-state veya deploy kararı değildir.

Operatörün dar failed-work/DLQ incelemesi için tablo yerine doğrudan değiştirilemeyen görünüm kullanılmalıdır:

```sql
SELECT
    event_id,
    attempt_count,
    failed_at,
    failure_code,
    failure_reason,
    accepted_at,
    envelope
FROM release_risk_ai_explanation_failed_work
ORDER BY failed_at, event_id
LIMIT 100;
```

Bu sorgu yalnız terminal işleri okur; pending/başarılı işleri, raw Kafka payload'unu veya claim token'larını açmaz. Sıralama yalnız operatör çıktısının deterministik olması içindir ve domain event ordering/latest-state anlamı taşımaz. Görünüme yazma PostgreSQL tarafından reddedilir; production operatör rolüne yalnız `SELECT` yetkisi verilmesi önerilir. Replay için bu görünümü veya inbox tablosunu elle update etmek desteklenen bir sözleşme değildir.

## Nasıl doğruladık?

Bu adım aşağıdaki sırayla doğrulanır:

1. V006 migration testinin mevcut V004→V005 pending işi kaybetmeden terminal alan/constraint/index ve salt-okunur failed-work görünümüne yükseltmesi; startup'ın exact V006 şemasını doğrulaması.
2. Processor options ve classifier testlerinin bounded `MaximumAttempts` değerini, retryable/terminal hata matrisini, capped exponential backoff'u ve enabled lease > HTTP timeout + state-update timeout bağıntısını doğrulaması.
3. Processor unit testlerinin başarı, retryable timeout, doğrudan terminal conflict, son-attempt terminalization, caller cancellation ve stale completion yollarını çalıştırması.
4. Gerçek PostgreSQL 16 testlerinin eşzamanlı ownership, timeout/backoff, cancellation, crash/restart limit sonlandırması, stale owner fencing, terminal duplicate idempotency, başarı/terminal karşılıklı dışlama, duplicate inbox kabulü, read-only DLQ sorgusu ve değişmeyen score/level/factor snapshot'ını doğrulaması.
5. Mevcut deterministic fake HTTP server ve gerçek local Uvicorn .NET→Python contract testlerinin transport/response sözleşmesini değişmeden koruması.
6. Python Ruff lint/format ve Python 3.9 bytecode compile kontrolleri, tüm Python testleri, `.NET format`, restore, warning-as-error build ve gerçek PostgreSQL/Redpanda dahil tüm .NET testlerinin tamamlanması; entegrasyon testlerinin Docker yokken sessizce atlanmaması.

Son doğrulamada Python Ruff lint/format/compile kontrolleri başarılı ve Python testleri `42/42 başarılı` oldu. `.NET format` ve restore başarılı, build `0 uyarı / 0 hata`; deterministic fake server, gerçek local Uvicorn process'i, PostgreSQL 16 ve Redpanda senaryoları dahil .NET testleri `213/213 başarılı, 0 atlanan` sonucunu verdi. Toplam `255` test başarılıdır. Testcontainers için Docker Engine ve Docker socket erişimi, cross-service test için README'deki Python `.venv` kurulumu gerekir; PostgreSQL, Kafka veya Python process entegrasyonları sessizce atlanmaz.

## Sıradaki küçük adım

Bu adım durable inbox sonrası AI açıklama retry, başarı ve terminal failed-work sınırında durur. Sonraki tek küçük adım yalnız `eventId` ile tek olayın AI açıklama durumunu okuyan dar bir HTTP query sözleşmesi olmalıdır:

- `pending`, `completed` veya `failed` durumlarından tam birini, başarıda mevcut açıklamayı, failure'da mevcut stabil kod/nedeni döndürmek.
- Inbox primary key'i dışındaki repository/PR latest-state veya listeleme/ordering anlamını eklememek.
- Manual replay/mutation, dashboard, deploy, retention ve outbox/Kafka yaşam döngüsünü kapsam dışında tutmak.

Sonraki checkpoint mevcut webhook kabulü, outbox yayını, Kafka V1 sözleşmesi, durable-accept-then-commit sırası ve immutable success/terminal sonuçlarını değiştirmemelidir.
