# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo artık doğrulanmış GitHub `pull_request` teslimatlarını PostgreSQL'e atomik olarak kabul eder, sürümlü outbox envelope'unu Kafka'ya at-least-once yayımlar, record'u durable inbox'a idempotent biçimde alır ve ayrı Python servisiyle bounded AI açıklaması üretir. Yetkili servisler tek event sonucunu, bounded keyset sayfalarını ve repository/change için açıkça `latestAccepted` olarak adlandırılmış snapshot'ı okuyabilir. Terminal başarısızlıklar ayrı credential, global bütçe ve `Idempotency-Key` ile yeni, değişmez replay generation'larına alınabilir; V006'daki özgün success/terminal sonuçları yerinde değiştirilmez. Düşük kardinaliteli query metrikleri opt-in OTLP ile collector'a aktarılabilir; bounded retention işi yalnız güvenle sonlandırılmış taşıma kayıtlarını temizler. Checked-in Docker Compose PostgreSQL, Redpanda, topic hazırlığı, iki uygulama ve OpenTelemetry Collector'dan oluşan tam yerel akışı çalıştırır. Dashboard ve production dağıtım/hardening bu backend tamamlamasının bilinçli olarak dışındadır.

## Bu adımda ne yapıyoruz?

- Mevcut query meter'ını yalnız explicit endpoint/protocol ile açılan, bounded interval/timeout kullanan OTLP exporter'a bağlıyoruz. Default kapalıdır; yalnız `ReleaseGuard.WebhookIngestion.Api` meter'ını dışarı taşır ve `/metrics` route'u açmaz.
- `GET /v1/release-risk-events/ai-explanations` ile opaque cursor'lı, `accepted_at DESC, event_id DESC` keyset sayfalama; repository/change route'unda ise anlamı response'ta da görünen `latestAccepted` seçimi sunuyoruz. İki route mevcut query active/previous authentication'ını ve aynı global read bütçesini paylaşır.
- `POST /v1/release-risk-events/{eventId}/ai-explanation/replays` ile yalnız son effective generation terminal failed ise idempotent replay oluşturuyoruz. Replay query credential'ından ayrı rotate edilebilir credential ve ayrı global per-instance limiter kullanır; özgün inbox sonucu mutate edilmez.
- V008 indeksleriyle çalışan opt-in retention worker yalnız yayımlanmış outbox, kalıcı inbox karşılığı bulunan eski accepted delivery ve eski ignored delivery receipt'lerini bounded batch'lerle temizler. Inbox, AI sonuçları, replay geçmişi, pending/claimed/unpublished kayıtlar silinmez.
- Tam yerel Compose yığını gerçek PostgreSQL/Redpanda hattını deterministic fake AI provider ve OTLP Collector ile ayağa kaldırır; secret'lar environment'dan zorunlu alınır ve kalıcı volume'lar açıkça yönetilir.
- Mevcut webhook HMAC, durable-accept-then-commit, Kafka offset, timeout/caller cancellation ve değişmez success/terminal sözleşmeleri korunur. Dashboard, Kubernetes/cloud manifesti, TLS/SASL secret yönetimi ve production HA bu adımda yoktur.

## Neden yapıyoruz?

Backend akışının yerelde tek komutla doğrulanabilmesi, operatörün sonucu bounded biçimde okuyabilmesi, terminal failure'ı geçmişi ezmeden yeniden deneyebilmesi ve bitmiş taşıma kayıtlarının sınırsız büyümemesi aynı güvenlik sınırını tamamlar. `latestAccepted` adı domain ordering iddiasını önler; replay generation modeli audit geçmişini korur; retention bağımlı kalıcı kayıtları silmez; OTLP ise credential veya `eventId` etiketi üretmeden kapasite ayarı için ölçüm sağlar. Bu özellikler dashboard veya production platformunun yerini tutmaz: limiter'lar ve worker'lar per-instance'dır, local Compose PLAINTEXT geliştirme ortamıdır ve deployment-wide kota/HA garantisi vermez.

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

`ignored` teslimatlar da `disposition = 'ignored'` ve null risk alanlarıyla saklanır. Bu seçim, doğrulanmış fakat bilinçli olarak desteklenmeyen bir teslimatın kabul edildiği gerçeğini restart ve instance'lar arasında korur. Saklanmasaydı aynı redelivery her seferinde yeni bir ignored kabul gibi görünürdü. Opt-in retention worker configured yaşı geçen ignored receipt'leri bounded temizleyebilir; archival/legal-hold politikası ise production kapsamına bırakılmıştır.

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

Foreign key `ON DELETE RESTRICT` kullanır. Bir delivery silindiğinde ilişkili event'in cascade ile sessizce kaybolması istenmez. Retention worker bu nedenle yalnız durable inbox karşılığı bulunan yayımlanmış outbox'ı önce, bağımlılığı kalmayan accepted delivery receipt'ini sonra siler. V002, V001'de zaten bulunan accepted satırları geriye dönük olarak outbox'a doldurmaz: yalnız yeni kod yoluyla V002 sonrasında kabul edilen delivery'ler event üretir.

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

Operatörün özgün V006 terminal sonuç sorgusu `release_risk_ai_explanation_failed_work` görünümüdür. Görünüm yalnız `event_id`, attempt/failed zamanı, failure kod/nedeni, `accepted_at` ve immutable `envelope` alanlarını taşır; raw payload, claim token'ı veya mutasyon yüzeyi açmaz. `DISTINCT` tanımı görünümü PostgreSQL seviyesinde doğrudan update edilemez kılar. Store'daki `ReadFailedWorkAsync(limit)` aynı sözleşmeyi `1–100` arasında bounded okur. Replay generation geçmişi ayrı `release_risk_ai_explanation_replay_history` görünümündedir; iki görünüm de mutasyon endpoint'i değildir. Migration rol/`GRANT` yönetmez; production'da operatör rolüne yalnız gereken görünüm için `SELECT` verilmelidir.

### Tek event AI açıklama query sözleşmesi

AI açıklama okuma route'larının üçü de tam olarak bir `Authorization` header değeri ve `Bearer` scheme'i ister. Authentication route/query değeri ayrıştırılmadan ve query portu çağrılmadan önce çalışır. Eksik header, boş/malformed değer, iki ayrı header değeri, proxy tarafından virgülle birleştirilmiş duplicate değer veya yanlış credential aynı `401` sonucuna iner; böylece istemciye hangi parçanın yanlış olduğu ya da event'in varlığı açıklanmaz. Başarısız yanıtta yalnız generic problem alanları ve stabil `code = ai_explanation_authentication_failed` vardır; `WWW-Authenticate: Bearer` challenge'ı hata alt türü taşımaz.

Başarılı authentication'dan sonraki kesin sıra `tek global rate-limit permit'i -> canonical D GUID doğrulaması -> bounded PostgreSQL read` biçimindedir. Limiter credential veya `eventId` almaz; application singleton'ı olduğu için active, previous ve bütün event kimlikleri aynı per-instance bütçeyi paylaşır. Authentication hataları permit tüketmez. Bütçe doluyken yanlış credential yine `401` alırken yetkili fakat malformed `eventId` de route ayrıştırılmadan `429` alır; reddedilen istekte query portu ve PostgreSQL hiç çağrılmaz.

Boundary, limiter singleton'ı oluşturulduğunda başlayan sabit pencerede `PermitLimit` kadar isteği atomik olarak kabul eder. Permit request bitince geri verilmez; pencere sınırına gelindiğinde bütün bütçe birlikte yenilenir. Kuyruk bulunmadığı için limit aşımı yeni bekleme veya cancellation semantiği oluşturmaz. Sabit pencere basittir fakat iki komşu pencerenin sınırında kısa bir aralıkta yaklaşık iki pencere bütçesi kadar burst görülebilir.

Active ve varsa previous credential en az 32, en fazla 512 karakterlik RFC Bearer-token alfabetine uygun, farklı yüksek entropili değerler olmalıdır. Doğrulayıcı yapılandırılmış açık değerleri saklamak yerine başlangıçta SHA-256 özetlerini alır. Sunulan değer tek kez aynı sabit uzunlukta özete çevrilir; active ve previous özetlerinin ikisi de her istekte `CryptographicOperations.FixedTimeEquals` ile karşılaştırılır, active eşleşmesinde erken dönülmez. Previous yapılandırılmadığında ikinci karşılaştırma yine sabit uzunluklu dummy özetle yapılır ve sonuç previous-yok bayrağıyla reddedilir. SHA-256 burada düşük entropili parolayı güvenli parola deposuna dönüştürmez; iki değer de secret manager'da üretilmiş yüksek entropili servis secret'ı olmalıdır. Bearer credential taşıma sırasında şifreleme sağlamadığından production trafiği uygulamada veya güvenilen gateway/service-mesh sınırında TLS kullanmalıdır.

Kesintisiz rotation sırası şöyledir:

1. Yeni binary alınmadan önce mevcut secret `AiExplanationQueryAuthentication:ActiveCredential` anahtarına taşınır; eski `Credential` anahtarı artık active fallback değildir.
2. Yeni secret üretilir ve bütün sunucu instance'ları `ActiveCredential = yeni`, `PreviousCredential = eski` ile rolling deploy edilir. Bu aşama tamamlanana kadar çağıranlar eski değeri kullanır.
3. Bütün instance'ların iki değeri kabul ettiği doğrulandıktan sonra çağıran servisler yeni active değere geçirilir.
4. Eski değeri kullanan çağıran kalmadığı dış deployment gözlemiyle doğrulanır; bütün sunucular `PreviousCredential` anahtarı kaldırılmış halde yeniden deploy edilir.

Uygulama previous için süre veya kullanım telemetrisi tutmaz; geçiş penceresinin gerçekten kısa kalması deployment sorumluluğudur. Instance'ların farklı active/previous çiftleriyle uzun süre çalışması tutarsız `401` üretebilir. Key kimliği response'a, log'a veya metriğe eklenmediği için hangi credential'ın eşleştiği istemciye açıklanmaz; bunun trade-off'u eski credential kullanımının bu servis içinden ayırt edilememesidir.

Doğrulanmış tek-event çağrısında `eventId` yalnız canonical tireli GUID (`D`) biçiminde kabul edilir. `IReleaseRiskExplanationQuery` repository veya PR alanı kullanmaz; `release_risk_event_inbox.event_id` primary key'iyle en fazla tek satırı ve yalnız gözlemlenebilir outcome kolonlarını okur. Replay generation varsa en yüksek generation'ın sonucu, yoksa V006 inbox sonucu effective state'tir. Raw Kafka payload'u, V1 envelope, score/factor snapshot'ı, attempt sayısı, claim token'ı ve zamanlar HTTP yanıtına açılmaz.

Pending satır için yanıt tam olarak şu şekildedir:

```json
{
  "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "status": "pending"
}
```

Pending; henüz claim edilmemiş, aktif/expired claim taşıyan veya retry backoff'unda bekleyen sonuçsuz satırların hepsini bilinçli olarak tek gözlemlenebilir duruma indirger. Bu endpoint worker sahipliği veya tahmini tamamlanma zamanı sözü vermez.

Completed satır mevcut event-bound açıklamayı değiştirmeden döndürür:

```json
{
  "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "status": "completed",
  "explanation": {
    "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
    "summary": "The recorded risk ...",
    "recommendations": ["Review primary_target_branch: ..."]
  }
}
```

Failed satır yalnız mevcut stabil terminal kod/nedeni döndürür:

```json
{
  "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "status": "failed",
  "failure": {
    "code": "response_contract_invalid",
    "reason": "AI explanation response violated the required response contract."
  }
}
```

Üç başarı şekli de `200 OK` döner ve null/alternatif sonuç alanı içermez: pending'de ne `explanation` ne `failure`, completed'da yalnız `explanation`, failed'da yalnız `failure` vardır. Store DB constraint'lerine ek olarak completed explanation'ın iç `eventId` eşleşmesini yeniden doğrular. Response katmanı recommendation listesini read-only kopyalar; her GET tek SQL statement'ının committed snapshot'ıdır ve hiçbir lifecycle alanını update etmez. Bir generation'ın pending snapshot'ı ileride completed veya failed olabilir; aynı generation'ın completed ve failed sonuçları fencing/DB constraint'leri gereği kalıcıdır. Replay yeni generation eklediğinde özgün V006 terminal sonucu değişmez, fakat query bilinçli olarak yeni effective generation'ı gösterir.

Hata sözleşmesi RFC problem JSON'una stabil `code` alanı ekler:

| Durum | HTTP / code | Anlam |
| --- | --- | --- |
| Eksik, malformed, duplicate veya yanlış service credential | `401` / `ai_explanation_authentication_failed` | Aynı generic body döner; route/query değerlendirilmez. |
| Yetkili istek için global pencere bütçesi dolu | `429` / `ai_explanation_rate_limit_exceeded` | Route/query değerlendirilmez; stabil body ile bounded `Retry-After` döner. |
| Route değeri canonical `D` GUID değil | `400` / `malformed_event_id` | DB sorgusu çalıştırılmaz. |
| GUID biçimi geçerli fakat inbox satırı yok | `404` / `ai_explanation_not_found` | Kimliğin webhook/outbox geçmişi hakkında ek bilgi verilmez. |
| `AiExplanationQuery:ReadTimeoutMilliseconds` doldu | `503` / `ai_explanation_query_timeout` | Yokluk veya lifecycle durumu uydurulmaz; istemci daha sonra güvenle tekrar okuyabilir. |
| Caller/request iptal edildi | HTTP sonucu üretilmez | Cancellation DB komutuna taşınır ve timeout/başarı olarak yeniden sınıflandırılmaz. |
| Timeout dışı beklenmeyen DB/contract hatası | Framework `5xx` | Bozuk durumu sahte pending/failed yanıtına çevirmek yerine hata görünür kalır. |

Limit aşımı body alan kümesi ve değerleri stabildir:

```json
{
  "title": "AI explanation request rate limit exceeded.",
  "status": 429,
  "detail": "The request rate limit was exceeded. Retry after the indicated delay.",
  "code": "ai_explanation_rate_limit_exceeded"
}
```

`Retry-After` tek bir pozitif tam sayı delta-seconds değeridir. Kalan pencere süresi yukarı yuvarlanır; değer en az `1`, en çok doğrulanmış `WindowMilliseconds` değerinin saniye tavanıdır ve bugünkü configuration üst sınırı nedeniyle `3600` saniyeyi aşamaz. Body credential, key, `eventId`, event varlığı, permit sayısı veya pencere ayrıntısı içermez ve `429` yanıtında authentication challenge verilmez.

Read deadline bağlantı havuzundan bağlantı alma, sorgu yürütme ve row okuma yolunun tamamına aynı linked cancellation token ile uygulanır. Deadline dolduğunda Npgsql komutu iptal edilir; in-flight SELECT herhangi bir mutasyon içermediği için belirsiz write sonucu yoktur. `503`, event'in bulunmadığı veya pending olduğu anlamına gelmez.

Authentication, rotation ve request limiter yalnız üç read route'una endpoint sınırında eklenmiştir. `/health` credential istemez ve rate-limit permit'i tüketmez; GitHub webhook'u kendi ham-gövde HMAC sözleşmesini kullanmaya devam eder, service credential kabul etmez ve bu bütçeye girmez. Active ve previous aynı çağıran servis yetkisini ve aynı bütçeyi temsil eder; servisleri birbirinden ayırmaz, kullanıcı/tenant kimliği veya rol matrisi üretmez. Read API polling orchestration, dashboard, deploy kararı veya outbox/Kafka yönetimi değildir.

Limiter process belleğinde ve **per-instance** çalışır. Birden fazla application instance'ının toplam throughput'u bu nedenle yaklaşık instance sayısıyla büyüyebilir; restart bütçeyi sıfırlar, rolling deployment sırasında pencere başlangıçları hizalanmayabilir ve configuration drift geçici farklı davranış yaratabilir. Bu uygulama deployment-wide toplam limit garantisi vermez. Böyle bir garanti gerekirse gateway/service-mesh veya paylaşımlı koordinasyon ayrı bir mimari sınır olarak eklenmelidir; mevcut dar limiter bu altyapıyı taklit etmez.

### AI açıklama query metrik sözleşmesi

Meter adı stabil `ReleaseGuard.WebhookIngestion.Api` değeridir. Bu checkpoint yalnız aşağıdaki beş instrument'ı üretir:

| Instrument | Tür / birim | Tag kümesi | Kayıt anı |
| --- | --- | --- | --- |
| `releaseguard.ai_explanation_query.authentication_failures` | `Counter<long>` / `{request}` | Tag yok | Credential doğrulanamadığında, mevcut `401` dönmeden önce. |
| `releaseguard.ai_explanation_query.rate_limit_permits` | `Counter<long>` / `{request}` | Tag yok | Başarılı authentication sonrası global permit alındığında; canonical GUID doğrulamasından önce. |
| `releaseguard.ai_explanation_query.rate_limit_rejections` | `Counter<long>` / `{request}` | Tag yok | Başarılı authentication sonrası permit reddedildiğinde, stabil `429` dönmeden önce. |
| `releaseguard.ai_explanation_query.outcomes` | `Counter<long>` / `{request}` | Yalnız `outcome = pending`, `completed`, `failed`, `not_found` veya `timeout` | Geçerli query sonucu response'a dönüştürüldüğünde, satır bulunmadığında veya bounded DB deadline dolduğunda. |
| `releaseguard.ai_explanation_query.database_read_duration` | `Histogram<double>` / `ms` | Tag yok | Query portu gerçekten çağrıldıysa `finally` yolunda; success, not-found, timeout, caller cancellation ve beklenmeyen exception dahil. |

Permit counter'ı rate-limit boundary'nin kararıdır; permit aldıktan sonra malformed GUID ile `400` olan istek de permit sayılır fakat DB histogramı veya outcome üretmez. Caller cancellation ve beklenmeyen query/contract exception'ı DB histogramına girer ama tamamlanmış bir HTTP/query sonucu olmadığı için outcome counter'ına girmez. Böylece outcome değer kümesi küçük ve anlamı nettir; permit ile outcome toplamlarının her zaman eşit olması beklenmez.

Metric API'si `eventId`, credential/key, `Authorization`, active/previous eşleşmesi, repository, terminal failure code/reason, response body, exception message veya caller/tenant bilgisi kabul etmez. Outcome dışındaki instrument'lar tamamen etiketsizdir; outcome ise enum üzerinden yalnız yukarıdaki beş değere çevrilir ve bilinmeyen değer reddedilir. Bu sınır hem secret/iş verisi sızıntısını hem de kontrolsüz time-series kardinalitesini engeller. Bedeli, hangi credential'ın veya event'in trafiği ürettiğinin bu servis metriğinden ayırt edilememesidir.

Instrument'lar .NET `IMeterFactory` üzerinden process içinde yayımlanır. Opt-in OpenTelemetry exporter yalnız bu meter adına abone olur ve OTLP/gRPC ya da OTLP/HTTP protobuf ile açıkça yapılandırılmış collector'a bounded periyotla gönderir. Export default kapalıdır; kapalıyken exporter pipeline'ı ve dış ağ I/O'su oluşmaz. Açıkken endpoint/protocol/interval/timeout startup'ta doğrulanır. Export hatası HTTP sonucuna çevrilmez; sonraki periodic export denenir. Repoda Prometheus `/metrics` route'u, exporter header/credential alanı, dashboard, alert veya SLO yoktur.

Counter/histogramlar instance kapsamındadır ve process restart/replica sınırlarını kendi başına birleştirmez. Filo toplamı, temporality, bucket görünümü ve retention seçimi collector/backend sorumluluğudur; bu veriler deployment-wide rate-limit garantisi veya domain ordering anlamı taşımaz. Query veya replay credential'ı OTLP authentication amacıyla yeniden kullanılmaz.

### Bounded listeleme ve açık `latestAccepted` seçimi

`GET /v1/release-risk-events/ai-explanations` yalnız `limit` ve `cursor` query parametrelerini kabul eder. `limit` default `50`, üst sınır `100`dür. Cursor; son satırın UTC `acceptedAt` ve `eventId` değerini base64url içinde taşıyan, canonical biçimi doğrulanan opaque bir continuation token'dır. Sayfa `accepted_at DESC, event_id DESC` keyset koşuluyla okunur; offset kullanılmaz. Response yalnız bounded `items` ve varsa `nextCursor` içerir. Her item event kimliği, effective status, kabul zamanı, repository, change number ve kind taşır; payload, credential, claim/retry ayrıntısı açmaz.

`GET /v1/repositories/{owner}/{repository}/changes/{changeNumber}/ai-explanation/latest-accepted`, aynı repository/change için PostgreSQL'e **en son kabul edilmiş** durable inbox snapshot'ını `accepted_at DESC, event_id DESC` ile seçer ve response'a `selection = latestAccepted` yazar. Bu sözleşme GitHub delivery sırası, commit ancestry, PR version'ı veya domain latest-state garantisi değildir; gecikmiş teslimat daha sonra kabul edilirse seçimi değiştirebilir. Böylece istemciye uygulamanın bilmediği bir event-ordering anlamı verilmez.

İki endpoint de tek-event query ile aynı active/previous authentication'ını, aynı global per-instance fixed-window bütçeyi ve aynı bounded DB deadline'ı paylaşır. Kesin sıra `authentication -> global permit -> parametre/route doğrulaması -> bounded PostgreSQL read` şeklindedir. Yanlış credential her zaman `401`; bütçe doluyken yetkili malformed liste/route `429`; permit alınmış geçersiz parametre ise stabil `400` olur. `429` veya parse hatasında DB çağrılmaz.

### Değişmez replay generation sözleşmesi

`POST /v1/release-risk-events/{eventId}/ai-explanation/replays` yalnız effective son generation terminal `failed` olduğunda yeni bir `pending` generation oluşturur. Route ayrı `AiExplanationReplayAuthentication` active/previous credential çiftini ve ayrı global per-instance fixed-window bütçeyi kullanır; read credential replay yetkisi vermez. Kesin sıra `replay authentication -> replay permit -> canonical eventId ve tek canonical Idempotency-Key -> bounded PostgreSQL transaction` biçimindedir.

`Idempotency-Key` canonical `D` GUID'dir ve kalıcı `replay_id` olur. Aynı key ve aynı event yeniden gönderilirse özgün `202` receipt aynı alan/değerlerle korunarak duplicate sonlandırılır; key başka event'e bağlıysa `409 ai_explanation_replay_id_conflict` döner. Transaction event satırını kilitler, effective sonucun eligibility'sini tekrar doğrular ve `(event_id, generation)` unique sınırı altında yeni generation ekler. Aynı key yarışları transaction-scoped advisory lock ile seri hale gelir. İlk kabul ve idempotent duplicate şu bounded shape'i döndürür:

```json
{
  "replayId": "6c2985a9-89a3-4a62-ab82-b6d92d40b657",
  "eventId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "generation": 1,
  "requestedAt": "2026-08-21T12:00:00Z",
  "status": "pending"
}
```

V007 `release_risk_ai_explanation_replays` tablosu request anındaki önceki failure ve exact envelope snapshot'ını, generation claim/retry/completed/failed lifecycle'ını ayrı saklar. Processor replay işlerini aynı bounded lease/fencing kurallarıyla çalıştırır. V006 inbox success/terminal kolonları update edilmez; `release_risk_ai_explanation_replay_history` görünümü generation geçmişini salt-okunur açar. `404` olmayan event'i, `409 ai_explanation_replay_not_eligible` pending/completed ya da replay'i zaten alınmış son state'i, `429` bounded `Retry-After`'ı, `503` ise DB deadline'ını bildirir. Caller cancellation timeout'a çevrilmez.

### Bounded retention sözleşmesi

Retention worker default `Enabled=false` başlar. Etkinleştirildiğinde her poll'da her kategori için en fazla configured batch kadar satırı, bounded DB timeout ve `FOR UPDATE SKIP LOCKED` ile temizler. Sıra ve güvenlik koşulları şunlardır:

1. Yalnız `published_at IS NOT NULL`, retention yaşını geçmiş ve karşılığında durable inbox satırı bulunan outbox kayıtları silinir.
2. Yalnız retention yaşını geçmiş, karşılığında durable inbox bulunan ve artık outbox kaydı kalmamış `accepted` webhook receipt'leri silinir.
3. Yalnız retention yaşını geçmiş `ignored` receipt'ler silinir.

Pending/claimed/unpublished outbox, inbox, AI success/terminal sonuçları, replay generation/history, migration kayıtları ve failed-work görünümünün dayandığı veriler silinmez. Bu nedenle retention bir AI result/history politikası değildir. V008 yalnız güvenli seçim sorgularını destekleyen kısmi indeksleri ekler. Birden çok instance aynı işi çalıştırabilir; `SKIP LOCKED` çakışmayı azaltır fakat schedule koordinasyonu, archival, legal hold, backup veya tablo partitioning sağlamaz. Compose local profilinde retention açık, normal uygulama default'unda kapalıdır.

Accepted delivery receipt'i silmek webhook sınırındaki duplicate response hafızasını configured retention ufkuyla sınırlar. Aynı eski GitHub delivery GUID'si bu ufuktan sonra yeniden gelirse webhook onu yeniden `accepted` görebilir ve yeni outbox handoff'u oluşturabilir; kalıcı inbox aynı `eventId` + exact payload tekrarını yine idempotent sonlandırır, farklı payload'ı ise güvenli olmayan conflict olarak reddeder. Bu nedenle accepted retention değeri GitHub redelivery/audit beklentisinden kısa seçilmemeli; uzun dönem raw webhook arşivi gerekiyorsa worker açılmadan önce ayrı production archival/legal-hold politikası kurulmalıdır.

### Tam yerel Docker Compose sınırı

`compose.yml`; PostgreSQL 16, Redpanda, tek topic hazırlama job'u, deterministic fake provider'lı Python AI API, .NET webhook/worker API ve OpenTelemetry Collector'ı tek proje altında çalıştırır. API startup migration'larını uygular; outbox dispatcher, inbox processor, AI processor, retention ve OTLP export local profilde açıktır. PostgreSQL/Redpanda named volume kullanır; topic auto-create yerine init container tarafından açıkça oluşturulur. Sağlık kontrolleri Compose dependency sırasını belirler.

Checked-in dosyada gerçek secret yoktur. PostgreSQL parolası, GitHub webhook secret'ı, query credential'ı ve replay credential'ı environment'dan zorunlu alınır. Kafka PLAINTEXT, collector authentication'sız ve AI provider fake'tir; portlar local geliştirme içindir. Bu yığın production deployment manifesti değildir ve TLS/SASL, secret manager, RBAC, replica/HA, backup, autoscaling ya da deployment-wide shared limiter sağlamaz.

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

AI query service credential'ları yalnız `AiExplanationQueryAuthentication:ActiveCredential` ve `AiExplanationQueryAuthentication:PreviousCredential` anahtarlarından gelir. Repoda default veya örnek secret bulunmaz; local ortamda environment configuration, production'da platform secret manager/configuration provider kullanılmalıdır. Active zorunludur; previous yalnız rotation penceresinde verilir. İki değer aynı biçim/sınır kurallarına uymalı ve birbirinden farklı olmalıdır. Eksik/geçersiz active, verilmiş fakat geçersiz previous veya aynı iki değer `IValidateOptions` + `ValidateOnStart` ile host'u durdurur. Trade-off: eski `Credential` anahtarına fallback ya da credential'sız compatibility modu yoktur; deployment yeni binary başlamadan mevcut secret'ı active anahtarına eşlemelidir.

Query rate-limit bütçesi credential secret'larından ayrı `AiExplanationQueryRateLimit` options bölümündedir. Permit ve pencere değerleri `IValidateOptions` + `ValidateOnStart` ile bounded doğrulanır; credential değeri, kimliği veya eşleşen active/previous bilgisi limiter configuration'ına ya da partition anahtarına taşınmaz.

Replay yetkisi ayrı `AiExplanationReplayAuthentication` active/previous çiftinden gelir; read credential hiçbir zaman mutasyon yetkisi değildir. OTLP endpoint/protocol ile retention/replay budget seçenekleri de credential options'larından ayrıdır. Checked-in configuration exporter header/API key alanı tanımlamaz; ağ katmanı authentication gerektiriyorsa bu local kapsamın dışında platform/collector sınırında çözülmelidir.

### Migration ve startup stratejisi

V001 `github_webhook_deliveries` tablosunu, V002 `release_risk_outbox_messages` tablosunu ve accepted-only ilişki constraint'lerini, V003 dispatcher yaşam döngüsünü, V004 `release_risk_event_inbox` tablosunu, V005 AI açıklama ownership/retry/başarı alanlarını, V006 terminal sonuç ve failed-work görünümünü, V007 ayrı replay generation/history yaşam döngüsünü, V008 ise retention seçim indekslerini oluşturur. Sekiz SQL dosyası build sırasında assembly'ye gömülür; `release_guard_schema_migrations` uygulanan sürümleri kaydeder. Varsayılan `PostgreSql:ApplyMigrationsOnStartup=false` davranışı DDL çalıştırmaz; migration sürümünün tam olarak uygulamanın beklediği V008 olduğunu, dört uygulama tablosunu ve iki görünümü doğrular. Böylece normal production runtime rolüne DDL yetkisi vermek zorunlu değildir.

Migration açıkça `true` yapıldığında uygulama transaction-scoped PostgreSQL advisory lock alır, migration metadata tablosunu oluşturur ve eksik migration'ları sürüm sırasıyla aynı transaction içinde uygular. Boş veritabanı V001→V002→V003→V004→V005→V006→V007→V008 yolundan geçer. V003 mevcut outbox satırlarını pending hale getirir; V004 yalnız Kafka'da gerçekten tüketilmiş record'u kabul saydığı için eski delivery/outbox satırlarını inbox'a backfill etmez. V005 mevcut inbox satırlarını pending açıklama işi yapar; V006 mevcut pending veya başarılı satırları sonucunu değiştirmeden terminal sözleşmesine yükseltir. V007 mevcut V006 satırlarını kopyalamaz veya mutate etmez; replay tablosu boş başlar. V008 veri değiştirmez, yalnız indeks ekler. Lock aynı deployment'taki DDL yarışını seri hale getirir. Runner yalnız ileri yönlü bilinen migration'ları uygular; down migration veya kapsamlı migration framework'ü iddia etmez.

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

Environment değişkenlerinde `:` yerine `__` kullanılır. Processor enabled değilse lifecycle satırları durable pending kalır; bu güvenli default bir başarı veya drop sayılmaz. Enabled instance batch içinde bounded paralellik kullanır. `MaximumAttempts`, aynı event için provider'ın kesin çağrı sayısı değildir: HTTP başlamadan önce crash olan claim de attempt sayılır, timeout sonrası uzak servis çağrıyı işlemiş olabilir. Bu muhafazakâr sınır sonsuz maliyeti engeller; operatör failed-work reason ve envelope üzerinden sonucu inceleyebilir. Processor backoff'unda jitter yoktur; manual replay ayrı authentication/idempotency sözleşmesiyle yeni generation oluşturur.

### .NET AI açıklama query, authentication ve rate-limit yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `AiExplanationQuery:ReadTimeoutMilliseconds` | Tek event PostgreSQL okumasının tamamı için `100–30000 ms`; default `5000`. |
| `AiExplanationQueryAuthentication:ActiveCredential` | Zorunlu `32–512` karakterlik yüksek entropili Bearer-token değeri; default yoktur. |
| `AiExplanationQueryAuthentication:PreviousCredential` | Yalnız rotation penceresinde isteğe bağlı; verilirse active ile aynı biçim kuralına uymalı ve ondan farklı olmalıdır. |
| `AiExplanationQueryRateLimit:PermitLimit` | Tek sabit penceredeki global per-instance istek bütçesi; `1–10000`, default `60`. |
| `AiExplanationQueryRateLimit:WindowMilliseconds` | Bütçe penceresi ve `Retry-After` üst sınırının kaynağı; `100–3600000 ms`, default `60000`. |

Environment karşılıkları `AiExplanationQuery__ReadTimeoutMilliseconds`, `AiExplanationQueryAuthentication__ActiveCredential`, geçici `AiExplanationQueryAuthentication__PreviousCredential`, `AiExplanationQueryRateLimit__PermitLimit` ve `AiExplanationQueryRateLimit__WindowMilliseconds` değerleridir. Credential için README veya checked-in appsettings değeri oluşturulmaz; deployment configuration anahtarlarını secret manager'dan host'a enjekte eder. Eksik/geçersiz rotation yapılandırması, sınır dışı timeout, permit veya pencere değeri `ValidateOnStart` sırasında uygulamayı durdurur. Limiter için disable ya da unbounded mode yoktur; anahtarlar verilmezse bounded `60` permit / `60000 ms` default'u kullanılır. Query deadline HTTP istemcisinin kendi timeout'undan bağımsızdır; istemci daha kısa sürede bağlantıyı keserse request cancellation önceliklidir ve `503` üretilmez. Timeout artırmak yavaş/kitlenmiş DB'yi sağlıklı hale getirmez; production timeout ve rate-limit değerleri bağlantı havuzu, normal query latency, polling ihtiyacı ve upstream timeout bütçesi birlikte ölçülerek seçilmelidir.

### OTLP metrics export yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `AiExplanationMetricsExport:Enabled` | Default `false`; kapalıyken exporter ve dış ağ I/O'su kurulmaz. |
| `AiExplanationMetricsExport:Endpoint` | Enabled iken credential/query/fragment taşımayan absolute HTTP/HTTPS URL zorunlu. |
| `AiExplanationMetricsExport:Protocol` | Enabled iken yalnız `grpc` veya `http/protobuf`; HTTP protobuf endpoint path'i `/v1/metrics` ile bitmelidir. |
| `AiExplanationMetricsExport:ExportIntervalMilliseconds` | `1000–300000`, default `60000`. |
| `AiExplanationMetricsExport:ExportTimeoutMilliseconds` | `100–30000`, default `10000`; export interval'ını aşamaz. |

Environment karşılıkları aynı adlarda `:` yerine `__` kullanır. Enabled olup endpoint/protocol eksikse ya da herhangi bir sınır ihlal edilirse `ValidateOnStart` host'u durdurur. Exporter yalnız `ReleaseGuard.WebhookIngestion.Api` meter'ını toplar; header/API key, sampling, custom histogram bucket'ı, trace/log pipeline'ı veya scrape route'u yapılandırılmaz.

### AI açıklama replay yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `AiExplanationReplayAuthentication:ActiveCredential` | Query credential'ından farklı, zorunlu `32–512` karakter Bearer-token secret'ı. |
| `AiExplanationReplayAuthentication:PreviousCredential` | Yalnız rotation sırasında isteğe bağlı; active'den farklı ve aynı biçimde olmalı. |
| `AiExplanationReplay:RequestTimeoutMilliseconds` | Replay transaction deadline'ı; `100–30000`, default `5000`. |
| `AiExplanationReplay:PermitLimit` | Tek global per-instance fixed window bütçesi; `1–1000`, default `10`. |
| `AiExplanationReplay:WindowMilliseconds` | Replay penceresi ve bounded `Retry-After` kaynağı; `100–3600000`, default `60000`. |

Environment karşılıkları `AiExplanationReplayAuthentication__ActiveCredential`, geçici `AiExplanationReplayAuthentication__PreviousCredential`, `AiExplanationReplay__RequestTimeoutMilliseconds`, `AiExplanationReplay__PermitLimit` ve `AiExplanationReplay__WindowMilliseconds` değerleridir. Credential secret'ları checked-in dosyada bulunmaz. Replay limiter disable/unbounded moda sahip değildir; query limiter'ından tamamen ayrıdır fakat kendi içinde active/previous aynı bütçeyi paylaşır.

### Retention cleanup yapılandırması

| Configuration anahtarı | Kural |
| --- | --- |
| `RetentionCleanup:Enabled` | Default `false`; Compose local profili açıkça `true` yapar. |
| `RetentionCleanup:BatchSize` | Kategori başına poll başına üst sınır; `1–1000`, default `100`. |
| `RetentionCleanup:PollIntervalMilliseconds` | `1000–86400000`, default `3600000`. |
| `RetentionCleanup:PublishedOutboxRetentionHours` | `1–87600`, default `168`. |
| `RetentionCleanup:AcceptedDeliveryRetentionHours` | `1–87600`, default `720`; outbox retention'dan kısa olamaz. |
| `RetentionCleanup:IgnoredDeliveryRetentionHours` | `1–87600`, default `168`. |
| `RetentionCleanup:CleanupTimeoutMilliseconds` | Her bounded cleanup çağrısı için `100–30000`, default `10000`. |

Environment karşılıklarında yine `__` kullanılır. Invalid batch/poll/retention/timeout host'u startup'ta durdurur. Enable bayrağı yalnız güvenli delete worker'ını açar; inbox veya AI/replay sonucunu temizleyen gizli bir mod yoktur.

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
├── .dockerignore
├── compose.yml
├── compose.kafka.yml
├── contracts/
│   └── release-risk-assessed.v1.example.json
├── Directory.Build.props
├── deploy/
│   └── local/
│       └── otel-collector-config.yml
├── global.json
├── ReleaseGuard.sln
├── scripts/
│   └── test-dotnet-python-contract.sh
├── src/
│   ├── ReleaseGuard.AiExplanation.Api/
│   │   ├── Dockerfile
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
│       ├── Dockerfile
│       ├── AiExplanationClientOptions.cs
│       ├── AiExplanationFailureClassifier.cs
│       ├── AiExplanationMetricsExporter.cs
│       ├── AiExplanationMetricsExportOptions.cs
│       ├── AiExplanationProcessorOptions.cs
│       ├── AiExplanationQueryAuthenticationOptions.cs
│       ├── AiExplanationQueryAuthenticator.cs
│       ├── AiExplanationQueryMetrics.cs
│       ├── AiExplanationQueryOptions.cs
│       ├── AiExplanationQueryRateLimitBoundary.cs
│       ├── AiExplanationQueryRateLimitOptions.cs
│       ├── AiExplanationReplayAuthentication.cs
│       ├── AiExplanationReplayOptions.cs
│       ├── AiExplanationReplayRateLimitBoundary.cs
│       ├── Database/Migrations/
│       │   ├── V001__create_github_webhook_deliveries.sql
│       │   ├── V002__create_release_risk_outbox.sql
│       │   ├── V003__add_release_risk_outbox_dispatch_lifecycle.sql
│       │   ├── V004__create_release_risk_event_inbox.sql
│       │   ├── V005__add_release_risk_ai_explanation_lifecycle.sql
│       │   ├── V006__add_release_risk_ai_explanation_terminal_lifecycle.sql
│       │   ├── V007__add_ai_explanation_replay_lifecycle.sql
│       │   └── V008__add_retention_cleanup_indexes.sql
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
│       ├── ReleaseRiskExplanationCollectionEndpoints.cs
│       ├── ReleaseRiskExplanationCollectionQuery.cs
│       ├── ReleaseRiskExplanationProcessor.cs
│       ├── ReleaseRiskExplanationQuery.cs
│       ├── ReleaseRiskExplanationQueryEndpoint.cs
│       ├── ReleaseRiskExplanationReplayEndpoint.cs
│       ├── ReleaseRiskExplanationReplayStore.cs
│       ├── ReleaseRiskExplanationStore.cs
│       ├── ReleaseGuardRetentionCleanup.cs
│       ├── ReleaseRiskOutboxDispatcher.cs
│       ├── ReleaseRiskOutboxEnvelope.cs
│       ├── ReleaseRiskOutboxStore.cs
│       ├── RetentionCleanupOptions.cs
│       └── VerifiedGitHubWebhook.cs
└── tests/
    └── ReleaseGuard.WebhookIngestion.Api.Tests/
        ├── BackendCompletionOptionsTests.cs
        ├── AiExplanationClientOptionsTests.cs
        ├── AiExplanationFailureClassifierTests.cs
        ├── AiExplanationProcessorOptionsTests.cs
        ├── AiExplanationQueryAuthenticationOptionsTests.cs
        ├── AiExplanationQueryAuthenticatorTests.cs
        ├── AiExplanationQueryMetricsTests.cs
        ├── AiExplanationQueryOptionsTests.cs
        ├── AiExplanationQueryRateLimitBoundaryTests.cs
        ├── AiExplanationQueryRateLimitOptionsTests.cs
        ├── GitHubWebhookEndpointTests.cs
        ├── HealthEndpointTests.cs
        ├── HttpReleaseRiskExplanationClientTests.cs
        ├── KafkaIntegrationFixture.cs
        ├── KafkaConsumerOptionsTests.cs
        ├── KafkaProducerOptionsTests.cs
        ├── KafkaReleaseRiskEventConsumerIntegrationTests.cs
        ├── KafkaReleaseRiskEventProducerIntegrationTests.cs
        ├── ManualTimeProvider.cs
        ├── OutboxDispatcherOptionsTests.cs
        ├── PostgreSqlAiExplanationQueryIntegrationTests.cs
        ├── PostgreSqlAiExplanationProcessorIntegrationTests.cs
        ├── PostgreSqlBackendCompletionIntegrationTests.cs
        ├── PostgreSqlGitHubWebhookIntegrationTests.cs
        ├── PostgreSqlInboxProcessorIntegrationTests.cs
        ├── PostgreSqlIntegrationFixture.cs
        ├── PostgreSqlOutboxDispatcherIntegrationTests.cs
        ├── PostgreSqlTestApplicationFactory.cs
        ├── PythonAiExplanationContractIntegrationTests.cs
        ├── ReleaseRiskEvaluatorTests.cs
        ├── ReleaseRiskExplanationQueryEndpointTests.cs
        ├── ReleaseRiskExplanationCollectionAndReplayEndpointTests.cs
        ├── ReleaseRiskExplanationProcessorTests.cs
        ├── ReleaseRiskInboxProcessorOptionsTests.cs
        ├── ReleaseRiskInboxProcessorTests.cs
        ├── ReleaseRiskOutboxDispatcherTests.cs
        ├── ReleaseRiskOutboxEnvelopeTests.cs
        ├── TestAiExplanationQueryMetrics.cs
        └── TestApplicationFactory.cs
```

Dashboard ve ek production altyapı klasörleri ihtiyaç doğduğu checkpoint'lerde oluşturulacaktır; boş yer tutucu klasörler eklenmemiştir.

## Tekrarlanabilir komutlar

Komutları bu README'nin bulunduğu `ReleaseGuardAI` klasöründe çalıştırın:

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

### Tam yerel Docker Compose

Docker Engine açıkken dört zorunlu secret'ı shell'e literal değer yazmadan üretip bütün hattı başlatın:

```bash
export RELEASEGUARD_POSTGRES_PASSWORD="$(openssl rand -hex 32)"
export RELEASEGUARD_GITHUB_WEBHOOK_SECRET="$(openssl rand -hex 32)"
export RELEASEGUARD_QUERY_CREDENTIAL="$(openssl rand -hex 32)"
export RELEASEGUARD_REPLAY_CREDENTIAL="$(openssl rand -hex 32)"

docker compose config --quiet
docker compose up --build --wait
docker compose ps
```

Default host portları PostgreSQL için `55432`, Redpanda için `19092`, webhook API için `8080`, AI API için `8090`dır. Gerekirse sırasıyla `RELEASEGUARD_POSTGRES_PORT`, `RELEASEGUARD_KAFKA_PORT`, `RELEASEGUARD_API_PORT` ve `RELEASEGUARD_AI_PORT` ile değiştirilebilir. Sağlık kontrolleri:

```bash
curl --fail http://localhost:8080/health
curl --fail http://localhost:8090/health
docker compose exec redpanda \
  rpk topic describe releaseguard.release-risk-assessed \
  -X brokers=redpanda:9092
docker compose exec postgres \
  psql -U postgres -d releaseguard -c \
  'SELECT max(version) AS schema_version FROM release_guard_schema_migrations;'
```

API/worker, AI ve OTLP sinyallerini incelemek için `docker compose logs webhook-api ai-explanation otel-collector`; yığını durdurup veriyi korumak için `docker compose down` kullanılır. Aşağıdaki komut **yalnız silinebilir local veri için** container'larla birlikte PostgreSQL ve Redpanda volume'larını da kaldırır:

```bash
docker compose down --volumes --remove-orphans
```

Compose, deterministic fake AI provider kullanır ve dış modele/ücretli servise bağlanmaz. PLAINTEXT Kafka, authentication'sız local collector ve environment secret'ları production güvenlik modeli değildir.

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

Yalnız AI açıklama query authentication/options/rate-limit/metrics/HTTP birim testleriyle gerçek PostgreSQL authorization, ortak bütçe, bounded list/latest, düşük kardinaliteli ölçüm, status, immutable read, timeout ve cancellation senaryolarını çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~ExplanationQuery
```

OTLP export, bounded collection/latest, immutable replay ve safe retention tamamlamalarını birlikte seçmek için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests \
  --filter 'FullyQualifiedName~BackendCompletion|FullyQualifiedName~CollectionAndReplay'
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

Başka bir terminalde en az 32 karakterlik webhook secret'ını, PostgreSQL bağlantısını, Kafka producer/consumer ayarlarını, birbirinden farklı query/replay service credential'larını ve ilk çalıştırma için migration bayrağını yapılandırın. Aşağıdaki bağlantı/parola değerleri yalnız local örnektir. Credential'lar literal yazılmaz; önceden secret provider veya `openssl rand -hex 32` ile oluşturulmuş `RELEASEGUARD_QUERY_CREDENTIAL` / `RELEASEGUARD_REPLAY_CREDENTIAL` environment değerlerinden geçirilir. Previous anahtarları yalnız rotation penceresinde farklı bir değerle eklenmelidir.

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
export AiExplanationQuery__ReadTimeoutMilliseconds=5000
export AiExplanationQueryAuthentication__ActiveCredential="${RELEASEGUARD_QUERY_CREDENTIAL:?query-credential-required}"
export AiExplanationQueryRateLimit__PermitLimit=60
export AiExplanationQueryRateLimit__WindowMilliseconds=60000
export AiExplanationReplayAuthentication__ActiveCredential="${RELEASEGUARD_REPLAY_CREDENTIAL:?replay-credential-required}"
export AiExplanationReplay__RequestTimeoutMilliseconds=5000
export AiExplanationReplay__PermitLimit=10
export AiExplanationReplay__WindowMilliseconds=60000
export RetentionCleanup__Enabled=false
export AiExplanationMetricsExport__Enabled=false
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
```

V001–V008 uygulandıktan sonra normal startup doğrulama modunu kullanın:

```bash
export PostgreSql__ApplyMigrationsOnStartup=false
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
```

Migration bayrağı hiç verilmezse `false` kabul edilir. V008'e ulaşmamış veritabanında false ile açılış bilinçli olarak başarısızdır; şema kendiliğinden veya bellek fallback'iyle oluşturulmaz. Kafka producer/consumer, worker, query/replay authentication+bütçe, OTLP ve retention sınırları geçersizse uygulama options validation ile startup'ta durur. `OutboxDispatcher__Enabled`, `InboxProcessor__Enabled`, `AiExplanationProcessor__Enabled`, `RetentionCleanup__Enabled` ve `AiExplanationMetricsExport__Enabled` verilmezse güvenli default `false` olur. Read endpoint'leri worker bayraklarından bağımsız committed snapshot'ı okur; query active/previous aynı read bütçesini, replay active/previous ise ayrı replay bütçesini paylaşır. Dispatcher publish hatalarını kalıcı capped backoff'a dönüştürür. AI processor retryable hataları configured attempt sınırına kadar dener; terminal sınıfları ve son retryable denemeyi kalıcı failed state'e taşır. Inbox processor etkin olduğunda ilk güvenli olmayan DB/contract/commit hatası worker'ı durdurur; Kafka offset commit'i AI çağrısını beklemez.

Başka bir terminalden sağlık kontrolü:

```bash
curl http://localhost:5080/health
```

Beklenen sağlık yanıtı:

```json
{"status":"ok","service":"webhook-ingestion"}
```

Durable inbox'a kabul edilmiş belirli bir olayın AI açıklama durumunu okumak için:

```bash
curl --fail-with-body \
  -H "Authorization: Bearer ${AiExplanationQueryAuthentication__ActiveCredential}" \
  http://localhost:5080/v1/release-risk-events/0b989ba4-242f-11e5-81e1-c7b6966d2516/ai-explanation
```

Çağıran servis active secret'ı kendi secret provider'ından almalıdır; credential URL/query içine konmamalı, loglanmamalı veya shell history'ye açık değer olarak yazılmamalıdır. Rotation penceresinde previous aynı response'u üretir; normal durumda previous anahtarı hiç bulunmamalıdır. Geçerli credential ile çağrı yalnız o `eventId` için yukarıdaki pending/completed/failed şekillerinden birini ya da bütçe doluysa stabil `429` + `Retry-After` döndürür. Polling istemcisi bu bounded gecikmeye uymalıdır. Inbox'a henüz kabul edilmemiş outbox/Kafka olayı `404` olur; query endpoint'i onu beklemez, publish etmez veya replay etmez.

Bounded liste ve açık latest-accepted sorguları:

```bash
curl --fail-with-body \
  -H "Authorization: Bearer ${AiExplanationQueryAuthentication__ActiveCredential}" \
  'http://localhost:5080/v1/release-risk-events/ai-explanations?limit=50'

curl --fail-with-body \
  -H "Authorization: Bearer ${AiExplanationQueryAuthentication__ActiveCredential}" \
  http://localhost:5080/v1/repositories/acme/ReleaseGuard/changes/42/ai-explanation/latest-accepted
```

Liste response'undaki `nextCursor` varsa sonraki istekte URL-encode edilerek `cursor` parametresine aynen verilmelidir; istemci cursor içeriğine anlam yüklememelidir. Latest route'un sonucu yalnız kabul zamanına göre `latestAccepted`tır.

Yalnız effective son state terminal failed ise replay istemek için her mantıksal istek başına tek yeni GUID üretin; transport retry'larında aynı anahtarı yeniden kullanın:

```bash
export REPLAY_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
curl --fail-with-body -X POST \
  -H "Authorization: Bearer ${AiExplanationReplayAuthentication__ActiveCredential}" \
  -H "Idempotency-Key: ${REPLAY_ID}" \
  http://localhost:5080/v1/release-risk-events/0b989ba4-242f-11e5-81e1-c7b6966d2516/ai-explanation/replays
```

Aynı `REPLAY_ID` aynı event için aynı alan/değerlerde `202` receipt döndürür; yeni key ile pending/completed state replay edilemez ve `409` olur. `429` alındığında bounded `Retry-After` beklenmelidir.

Read çağrıları stabil meter instrument'larını process içinde üretir. OTLP'yi el ile açmak için collector endpoint/protocol ve bounded periyotları açıkça verin:

```bash
export AiExplanationMetricsExport__Enabled=true
export AiExplanationMetricsExport__Endpoint='http://localhost:4317'
export AiExplanationMetricsExport__Protocol='grpc'
export AiExplanationMetricsExport__ExportIntervalMilliseconds=60000
export AiExplanationMetricsExport__ExportTimeoutMilliseconds=10000
```

Query veya replay credential'ı metrics export için yeniden kullanılmamalıdır. Export açık olsa da `/metrics` route'u oluşmaz ve collector erişilemezliği HTTP query body/status'unu değiştirmez.

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

Bu backend tamamlaması aşağıdaki sınırlarla doğrulanır:

1. OTLP options default/sınır/fail-fast testleri ve gerçek HTTP protobuf collector testi yalnız stabil query meter/instrument'larının export edildiğini; kapalı modun dış I/O yapmadığını doğrular.
2. Read endpoint testleri authentication'ın `401` önceliğini, active/previous ortak global bütçeyi, malformed parametrelerin DB öncesinde sonlanmasını, cursor canonicality/keyset sınırını, `latestAccepted` seçimini ve timeout/caller cancellation ayrımını kapsar.
3. Replay testleri ayrı credential'ın zorunlu olduğunu, read credential'ın reddedildiğini, active/previous ortak replay bütçesini, canonical `Idempotency-Key`, stabil `202/400/404/409/429/503` sözleşmelerini ve aynı key yarışında tek generation oluşmasını kanıtlar.
4. Gerçek PostgreSQL testleri replay sonrası V006 terminal satırının byte/kolon olarak değişmediğini, processor'ın yeni generation'ı tamamladığını ve effective query'nin yeni sonucu gösterdiğini doğrular.
5. Retention options/store testleri yalnız yayımlanmış+durable outbox, bağımlılığı kalmamış accepted receipt ve eski ignored receipt'in silindiğini; pending/claimed/unpublished outbox, inbox ve replay history'nin korunduğunu doğrular.
6. Mevcut webhook HMAC/idempotency, V1 outbox/Kafka, durable-accept-then-commit, AI retry/terminal/DLQ, query `401/200/400/404/429/503`, `/health` ve gerçek local Uvicorn regresyon paketleri aynen çalışır.
7. `docker compose config`, iki image build'i ve `docker compose up --wait` sonrasında health, V008 şema, topic, imzalı GitHub webhook → Kafka → inbox → completed AI query ve collector'daki üç query metriği gerçek container'larla uçtan uca doğrulanır; ardından local test volume'ları temizlenir.
8. Python Ruff lint/format/compile + tüm Python testleri ile `.NET format`, restore, warning-as-error build ve Testcontainers PostgreSQL/Redpanda dahil tüm .NET testleri tamamlanır; entegrasyonlar Docker yokken sessizce atlanmaz.

Son doğrulamada Python Ruff lint/format/compile kontrolleri ve `42/42` Python testi başarılı oldu. `.NET format` ve restore tamamlandı; warning-as-error build `0 uyarı / 0 hata`, gerçek PostgreSQL 16, Redpanda, OTLP collector listener'ı ve local Uvicorn sözleşmesi dahil .NET paketi `323/323 başarılı, 0 atlanan` sonucunu verdi. Toplam `365` test başarılıdır. Ayrıca `docker compose config`, iki image build'i ve tam yığın `up --wait` koşusu; V008 şema, topic, imzalı webhook'tan completed AI açıklamasına kadar uçtan uca akış ve OTLP metric export ile doğrulandı. Sonunda yalnız `releaseguard-local` test container/network/volume'ları kaldırıldı.

## Sıradaki küçük adım

README'deki backend checkpoint zinciri ve tam local Compose akışı tamamlanmıştır. Kullanıcı kararı gereği sıradaki iki ayrı ürün fazı şimdi uygulanmaz:

- Dashboard: salt-okunur API tüketimi, kullanıcı/tenant authorization modeli ve UX ihtiyaçları ayrı ürün sözleşmesiyle ele alınmalıdır.
- Production: TLS/SASL, secret manager, DB/Kafka/collector HA, deployment-wide shared limiting, migration job/least privilege, backup/restore, retention/legal-hold politikası, alarm/SLO ve orchestration manifestleri hedef platform seçildikten sonra tasarlanmalıdır.

Bu fazlardan biri açıkça başlatılana kadar backend sözleşmesine yeni route, rol matrisi, deploy kararı veya platform varsayımı eklenmemelidir.
