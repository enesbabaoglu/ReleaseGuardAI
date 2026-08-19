# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo şu anda **Checkpoint 3: doğrulanmış GitHub teslimat sözleşmesi ve süreç-içi idempotency** aşamasına ulaşmıştır. Webhook uç noktası imzayı doğruladıktan sonra teslimat kimliğini, olay türünü ve JSON payload'ını açık bir sözleşmeye dönüştürür; aynı teslimatın tekrarını ikinci kez kabul etmez. Kafka, PostgreSQL, normalize edilmiş risk event'leri, Python AI servisi ve dashboard henüz eklenmemiştir.

## Bu adımda ne yapıyoruz?

- İmza kontrolünü geçen isteği `VerifiedGitHubWebhook` sözleşmesine dönüştürüyoruz.
- Sözleşmede `X-GitHub-Delivery` kaynaklı bir `Guid`, `X-GitHub-Event` kaynaklı olay adı ve değişmez bir JSON payload snapshot'ı taşıyoruz.
- Eksik/bozuk metadata ile geçersiz JSON'u kaydetmeden reddediyoruz.
- Aynı teslimat GUID'sini süreç içinde atomik olarak yalnızca bir kez kaydediyor, tekrarını başarılı fakat `duplicate` olarak yanıtlıyoruz.
- Tüm akışları gerçek ASP.NET Core test host'u üzerinden doğruluyoruz.

## Neden bu kadar küçük başlıyoruz?

GitHub başarısız ya da manuel yeniden teslimatlarda aynı webhook'u tekrar gönderebilir. Kimlik kontrolü olmadan ileride aynı analiz, kayıt veya bildirim birden fazla üretilebilir. Idempotency, aynı teslimatın tekrar edilmesinin ikinci bir işleme yol açmaması demektir. Önce bu davranışı küçük bir süreç-içi sınırda testlerle sabitlemek, kalıcı altyapı eklenmeden sözleşmeyi ve HTTP davranışını görünür kılar.

## Mimari kararlar

### Neden ham gövdeyi doğruluyoruz?

GitHub imzayı gönderdiği byte dizisi üzerinden üretir. JSON önce deserialize edilip yeniden oluşturulursa boşluklar, alan sırası veya karakter kodlaması değişebilir ve doğru imza reddedilebilir. Bu nedenle doğrulayıcı `Request.Body` akışını doğrudan HMAC hesabına verir.

Bu checkpoint'te aynı byte'ları imza doğrulamasından sonra JSON olarak okuyabilmek için ASP.NET Core request buffering etkinleştirilir. İmza yine ham akış üzerinden hesaplanır; akış daha sonra başa sarılıp ayrıştırılır. Framework küçük gövdeleri bellekte, eşik üzerindekileri geçici dosyada tutabilir.

Trade-off: istek iki kez okunur ve buffering ek bellek/disk I/O maliyeti getirir. Ayrıca JSON ağacının kendisi bellekte tutulur. Bu küçük kabul sınırı için sadedir; yoğun trafikte streaming ayrıştırma, açık payload boyutu sınırı ve ölçüm ayrıca değerlendirilmelidir.

### Domain sözleşmesi neden bu kadar genel?

`VerifiedGitHubWebhook`, yalnızca bu sınırda güvenle bildiğimiz üç alanı taşır: teslimat GUID'si, olay adı ve JSON payload. Payload `JsonElement.Clone()` ile belge ömründen bağımsız, değişmez bir snapshot olur. JSON kökünün nesne olması zorunludur.

Olay adı enum yapılmadı; GitHub zaman içinde yeni olay türleri ekleyebildiği için enum bilinmeyen ama geçerli teslimatları gereksiz yere reddeder. Pull request, push, CI veya deployment payload'larını ReleaseGuard'ın daha kararlı ve sağlayıcıdan bağımsız risk event'lerine dönüştürmek ayrı bir sonraki adımdır. Bu seçim erken aşamada onlarca GitHub payload sınıfı üretme maliyetinden kaçınır; trade-off olarak bu sözleşme henüz alan bazlı compile-time güvence sağlamaz.

### Idempotency anahtarı ve sınırı

GitHub, `X-GitHub-Delivery` değerini teslimatı tanımlayan global benzersiz kimlik (GUID) olarak belgeler ve redelivery sırasında aynı değerin korunduğunu belirtir. Bu nedenle anahtar payload hash'i değil bu GUID'dir. Kaynaklar: [webhook header sözleşmesi](https://docs.github.com/en/webhooks/webhook-events-and-payloads) ve [webhook iyi uygulamaları](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks).

`InMemoryGitHubWebhookDeliveryRegistry`, `ConcurrentDictionary.TryAdd` ile eşzamanlı iki aynı istekte bile yalnızca bir kaydın ilk olmasını sağlar. Buradaki atomik işlem, iki isteğin araya girip ikisinin de “ilk” sayılmasını önleyen tek ve bölünmez kayıt adımıdır. Kayıt ancak imza, metadata ve JSON doğrulamaları tamamlandıktan sonra yapılır; hatalı bir istek GUID'yi rezerve etmez.

Bu **kalıcı idempotency değildir**: süreç yeniden başladığında geçmiş unutulur, birden fazla uygulama instance'ı aynı belleği paylaşmaz ve kayıtlar henüz zamanla temizlenmez. Üretim sınırında teslimat GUID'si veritabanında benzersiz anahtar olmalı; kayıt ile sonraki yan etki aynı transaction/outbox sınırında tasarlanmalıdır. Bu checkpoint “exactly once” garantisi iddia etmez.

### Neden HMAC-SHA256 ve sabit-zamanlı karşılaştırma?

GitHub'ın `X-Hub-Signature-256` sözleşmesi `sha256=<64 hex karakter>` biçimindedir. Sunucu aynı secret ve ham gövdeyle HMAC-SHA256 üretir. İki digest normal eşitlik operatörüyle değil `CryptographicOperations.FixedTimeEquals` ile karşılaştırılır; böylece eşleşen prefix uzunluğundan bilgi sızdıran timing saldırısı riski azaltılır.

Trade-off: yalnızca GitHub'ın SHA-256 imza şeması kabul edilir; eski `X-Hub-Signature`/SHA-1 başlığı bilinçli olarak desteklenmez.

### Secret neden options/configuration içinde?

Kod yalnızca `GitHubWebhook:Secret` yapılandırma anahtarını bilir; gerçek secret repoya yazılmaz. .NET configuration hiyerarşisi sayesinde yerelde `GitHubWebhook__Secret` ortam değişkeni, üretimde ise ortamın secret manager/configuration provider'ı kullanılabilir. Uygulama secret boşsa veya 32 karakterden kısaysa `ValidateOnStart` ile açılmaz; yanlış yapılandırmayla güvensiz çalışmak yerine fail-fast davranır.

Minimum uzunluk tahmin edilmesi kolay secret riskini azaltır. Trade-off: eski ve kısa bir secret doğrudan kullanılamaz; en az 32 karakterlik yüksek entropili yeni bir değer üretilmelidir. Ortam değişkeni pratik bir yerel geliştirme seçeneğidir; üretimde platformun secret manager'ı tercih edilmelidir.

### HTTP durumları neden böyle?

| Durum | Yanıt | Gerekçe |
| --- | --- | --- |
| İmza ve sözleşme geçerli, GUID yeni | `202 Accepted` + `accepted` receipt | Teslimat bu süreçte ilk kez kaydedildi. |
| GUID daha önce kaydedilmiş | `200 OK` + `duplicate` receipt | Tekrar güvenli biçimde sonlandırıldı; gönderici bunu hata sayıp yeniden denemez. |
| İmza başlığı eksik | `401 Unauthorized` | İstek gerekli kimlik doğrulama kanıtını taşımıyor. |
| İmza şeması/uzunluğu/hex biçimi bozuk | `400 Bad Request` | İstemci imza protokolüne uymayan bir değer gönderdi. |
| Biçimi doğru fakat digest yanlış | `401 Unauthorized` | Kimlik doğrulama başarısız; ayrıntılı karşılaştırma bilgisi açıklanmaz. |
| İmza geçerli fakat teslimat/olay başlığı eksik, GUID bozuk veya JSON geçersiz | `400 Bad Request` | Güvenlik kontrolünü geçen istek domain sözleşmesine dönüştürülemedi. |

`X-Hub-Signature-256` tamamen eksikse `401`; imza başlığı var fakat biçimi bozuksa `400` davranışı korunur. Geçerli istekte henüz risk analizi veya harici event yayını yapılmaz. İlk teslimattaki `202`, doğrulanmış sözleşmenin süreç-içi kayda alındığını bildirir.

### Neden monorepo?

ReleaseGuard'ın .NET servisleri, Python AI servisi, olay sözleşmeleri ve ilerideki dashboard'u aynı ürünün parçalarıdır. Erken aşamada tek depo kullanmak:

- bir olay sözleşmesi değiştiğinde üretici, tüketici ve testleri tek değişiklikte güncellemeyi,
- tek bir yerel geliştirme ve CI giriş noktası sunmayı,
- mimari kararları ve sürüm geçmişini birlikte tutmayı

kolaylaştırır.

Trade-off şudur: depo büyüdükçe tüm projeleri her değişiklikte derlemek pahalılaşabilir ve servis sınırları bulanıklaşabilir. Bunu net klasör/proje sınırları, bağımlılık kuralları ve ileride path-filtered CI ile yöneteceğiz. Takımların yayın döngüleri gerçekten bağımsızlaşırsa bazı bileşenleri ayrı depolara taşımak yeniden değerlendirilebilir.

### Neden şimdilik .NET 8?

Hedef teknoloji .NET 10'dur; ancak incelenen makinede yalnızca .NET SDK `8.0.416` ve .NET runtime `8.0.22` kurulu. Çalışmadığı doğrulanamayan `net10.0` dosyaları üretmek yerine ilk checkpoint `net8.0` ile derlenebilir tutuldu. `global.json` kullanılan SDK'yı sabitler. .NET 10 SDK kurulduğunda hedef framework ayrı bir yükseltme adımında değiştirilecek ve tüm testler yeniden çalıştırılacaktır.

### Neden hâlâ Kafka veya veritabanı yok?

Bu turda çözdüğümüz problem doğrulanmış teslimat sözleşmesi ile tekrar algılama davranışıdır. Kafka ve PostgreSQL; bağlantı yönetimi, migration, transaction ve yeniden deneme gibi ayrı hata modları getirir. Önce idempotency semantiğini süreç içinde testlerle sabitlemek, daha sonra kalıcı depoyu aynı arayüzün arkasında eklemeyi kolaylaştırır. Bellek implementasyonu üretim alternatifi olarak sunulmaz.

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
ReleaseGuard/
├── Directory.Build.props
├── global.json
├── ReleaseGuard.sln
├── src/
│   └── ReleaseGuard.WebhookIngestion.Api/
│       ├── GitHubWebhookDeliveryRegistry.cs
│       ├── GitHubWebhookEndpoint.cs
│       ├── GitHubWebhookOptions.cs
│       ├── GitHubWebhookReceipt.cs
│       ├── GitHubWebhookSignatureValidator.cs
│       └── VerifiedGitHubWebhook.cs
└── tests/
    └── ReleaseGuard.WebhookIngestion.Api.Tests/
        ├── GitHubWebhookEndpointTests.cs
        ├── HealthEndpointTests.cs
        └── TestApplicationFactory.cs
```

Python servisi, altyapı ve dashboard klasörleri ihtiyaç doğduğu checkpoint'lerde oluşturulacaktır; boş yer tutucu klasörler eklenmemiştir.

## Tekrarlanabilir komutlar

Komutları bu README'nin bulunduğu `ReleaseGuard` klasöründe çalıştırın:

```bash
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

Yalnızca webhook entegrasyon testlerini çalıştırmak için:

```bash
dotnet test tests/ReleaseGuard.WebhookIngestion.Api.Tests --filter FullyQualifiedName~GitHubWebhookEndpointTests
```

Servisi çalıştırmadan önce en az 32 karakterlik bir secret yapılandırın. Aşağıdaki değer yalnızca yer tutucudur; gerçek ortamda rastgele üretilmiş bir secret ve üretim platformunun secret manager'ını kullanın:

```bash
export GitHubWebhook__Secret='replace-with-a-random-secret-of-at-least-32-characters'
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
```

Başka bir terminalden sağlık kontrolü:

```bash
curl http://localhost:5080/health
```

Beklenen sağlık yanıtı:

```json
{"status":"ok","service":"webhook-ingestion"}
```

GitHub webhook ayarında payload URL'sini `/webhooks/github`, content type'ı `application/json` ve secret'i uygulamaya verilen değerle aynı ayarlayın. GitHub her teslimatta gerekli `X-Hub-Signature-256`, `X-GitHub-Delivery` ve `X-GitHub-Event` başlıklarını gönderir. İlk geçerli teslimatın örnek yanıtı şöyledir:

```json
{
  "deliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "eventName": "pull_request",
  "status": "accepted"
}
```

Aynı GUID tekrar gelirse `status` değeri `duplicate` olur. Otomatik testler imza hatalarına ek olarak sözleşme alanlarını, JSON ayrıştırmayı ve tekrar teslimatı gerçek HTTP istekleriyle kapsar; manuel HMAC üretimi doğrulama için gerekli değildir.

## Nasıl doğruladık?

Checkpoint aşağıdaki sırayla doğrulanır:

1. Restore ile NuGet bağımlılıklarının çözümlenmesi.
2. Solution build ile warning-as-error dahil tüm projelerin derlenmesi.
3. Gerçek ASP.NET Core test host'u üzerinden sağlık ve webhook senaryolarının çalışması.

Son doğrulamada build `0 uyarı / 0 hata`, testler `12/12 başarılı` sonucunu verdi. Kısıtlı sandbox'ta test runner yerel IPC soketi açmak için ek izin isteyebilir; bu uygulamanın ağ bağımlılığı olduğu anlamına gelmez.

## Sıradaki küçük adım

Bu checkpoint burada durur. Sonraki küçük adımda desteklenecek ilk GitHub event türü ve `action` seçilip genel JSON envelope'undan ReleaseGuard'a özgü, normalize edilmiş bir risk girdisine dönüşüm tasarlanabilir. Kalıcı idempotency deposu ise ilk gerçek yan etki veya çoklu-instance çalışma eklenmeden önce benzersiz anahtar ve transaction sınırıyla ele alınmalıdır.
