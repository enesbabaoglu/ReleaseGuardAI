# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo şu anda **Checkpoint 4: ilk normalize edilmiş ReleaseGuard risk girdisi** aşamasına ulaşmıştır. Webhook uç noktası önce GitHub imzasını ve genel teslimat sözleşmesini doğrular; ardından ilk desteklenen kombinasyon olan `pull_request` / `opened` payload'ını dar bir `ReleaseRiskInput` nesnesine dönüştürür. Kafka, PostgreSQL, gerçek risk puanlama motoru, Python AI servisi ve dashboard henüz eklenmemiştir.

## Bu adımda ne yapıyoruz?

- İlk desteklenen GitHub `event` / `action` çiftini `pull_request` / `opened` olarak seçiyoruz. `Event`, “pull request” gibi olay ailesidir; `action` ise bu olayın “açıldı” gibi alt türüdür.
- Doğrulanmış genel JSON `payload`'ını, yani isteğin veri gövdesini, ReleaseGuard'ın ihtiyaç duyduğu az sayıdaki tutarlı alana dönüştürüyoruz. Buna **normalizasyon** denir.
- Üretilen `ReleaseRiskInput`; kaynak teslimat kimliği, depo, değişiklik numarası, başlık, yazar, kaynak/hedef dal, draft durumu, değişen dosya ve eklenen/silinen satır sayılarını taşır.
- Desteklenmeyen olay veya action'ı hata saymadan `ignored`, desteklenen kombinasyondaki eksik/yanlış alanları `400 Bad Request` olarak yanıtlıyoruz.
- Mevcut imza kontrolü ve süreç-içi tekrar engelleme davranışını koruyor; tüm akışı gerçek ASP.NET Core test host'u üzerinden doğruluyoruz.

## Neden yapıyoruz?

GitHub payload'ları çok geniştir ve GitHub'a özgü iç içe alanlar taşır. Risk değerlendirme kodu doğrudan bu yapıya bağlanırsa hem anlaşılması zorlaşır hem de sağlayıcı değişikliklerinden kolay etkilenir. Dar bir risk girdisi, sonraki puanlama adımının yalnızca ihtiyaç duyduğu kavramları görmesini sağlar. Tek event/action ile başlamak ise hangi alanların gerçekten gerekli olduğunu testlerle öğrenmemize, henüz ihtiyaç duyulmayan onlarca payload modeli üretmememize yardımcı olur.

## Mimari kararlar

### Neden ham gövdeyi doğruluyoruz?

GitHub imzayı gönderdiği byte dizisi üzerinden üretir. JSON önce deserialize edilip yeniden oluşturulursa boşluklar, alan sırası veya karakter kodlaması değişebilir ve doğru imza reddedilebilir. Bu nedenle doğrulayıcı `Request.Body` akışını doğrudan HMAC hesabına verir.

Bu checkpoint'te aynı byte'ları imza doğrulamasından sonra JSON olarak okuyabilmek için ASP.NET Core request buffering etkinleştirilir. İmza yine ham akış üzerinden hesaplanır; akış daha sonra başa sarılıp ayrıştırılır. Framework küçük gövdeleri bellekte, eşik üzerindekileri geçici dosyada tutabilir.

Trade-off: istek iki kez okunur ve buffering ek bellek/disk I/O maliyeti getirir. Ayrıca JSON ağacının kendisi bellekte tutulur. Bu küçük kabul sınırı için sadedir; yoğun trafikte streaming ayrıştırma, açık payload boyutu sınırı ve ölçüm ayrıca değerlendirilmelidir.

### Genel envelope ile risk girdisi neden ayrı?

`VerifiedGitHubWebhook`, güvenlik ve taşıma sınırıdır; teslimat GUID'sini, olay adını ve değişmez JSON snapshot'ını taşımaya devam eder. `ReleaseRiskInput` ise ReleaseGuard'ın iş sınırıdır; GitHub'ın iç içe nesne adlarını sonraki risk koduna sızdırmaz. Böylece imza/HTTP ayrıntıları ile risk değerlendirme kavramları birbirine karışmaz.

`GitHubRiskInputMapper` yalnızca `pull_request` / `opened` kombinasyonunu bir `change_opened` girdisine çevirir. Ek JSON alanları yok sayılır; gerekli alanların türü ve temel sınırları açıkça doğrulanır. Desteklenmeyen bir event/action geçerli bir GitHub teslimatı olabileceği için `ignored` kabul edilir. Buna karşılık desteklendiğini söylediğimiz `opened` payload'ı eksikse sessizce veri uydurmak yerine `400` döner ve teslimat GUID'si rezerve edilmez.

Trade-off: ilk model risk için yararlı değişiklik büyüklüğünü taşır fakat dosya yolları, commit listesi, etiketler veya review bilgisi taşımaz. Ayrıca şu anda yalnızca GitHub kaynaklıdır ve normalize edilmiş girdiyi henüz bir puanlayıcıya ya da kalıcı kuyruğa vermez; `202` yanıtındaki `riskInput` dönüşümü görünür ve test edilebilir kılar. Yeni alanlar gerçek bir risk kuralı tarafından gerekçelendirilmeden modele eklenmeyecektir.

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
| Desteklenen event/action ve sözleşme geçerli, GUID yeni | `202 Accepted` + `accepted` receipt | Teslimat kaydedildi ve normalize edildi. |
| Teslimat geçerli fakat event/action desteklenmiyor | `202 Accepted` + `ignored` receipt | Göndericinin yeniden denemesine yol açmadan bilinçli olarak işlenmedi. |
| GUID daha önce kaydedilmiş | `200 OK` + `duplicate` receipt | Tekrar güvenli biçimde sonlandırıldı; gönderici bunu hata sayıp yeniden denemez. |
| İmza başlığı eksik | `401 Unauthorized` | İstek gerekli kimlik doğrulama kanıtını taşımıyor. |
| İmza şeması/uzunluğu/hex biçimi bozuk | `400 Bad Request` | İstemci imza protokolüne uymayan bir değer gönderdi. |
| Biçimi doğru fakat digest yanlış | `401 Unauthorized` | Kimlik doğrulama başarısız; ayrıntılı karşılaştırma bilgisi açıklanmaz. |
| İmza geçerli fakat teslimat/olay başlığı eksik, GUID bozuk, JSON geçersiz veya desteklenen payload eksik | `400 Bad Request` | Güvenlik kontrolünü geçen istek gerekli sözleşmeye dönüştürülemedi. |

`X-Hub-Signature-256` tamamen eksikse `401`; imza başlığı var fakat biçimi bozuksa `400` davranışı korunur. İlk desteklenen teslimattaki `202`, doğrulanmış sözleşmenin süreç-içi kayda alındığını ve risk girdisine dönüştürüldüğünü bildirir; henüz risk skoru veya harici event yayını üretilmez.

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

Bu turda çözdüğümüz problem, GitHub'a özgü geniş JSON'dan ReleaseGuard'a özgü dar girdiye doğru dönüşümdür. Kafka ve PostgreSQL; bağlantı yönetimi, migration, transaction ve yeniden deneme gibi ayrı hata modları getirir ve dönüşüm kararını doğrulamak için gerekli değildir. Bellek idempotency implementasyonu üretim alternatifi olarak sunulmaz; ilk gerçek yan etki veya çoklu-instance çalışma eklenmeden önce kalıcı benzersiz anahtar ve transaction sınırı tasarlanmalıdır.

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
│       ├── GitHubRiskInputMapper.cs
│       ├── GitHubWebhookSignatureValidator.cs
│       ├── ReleaseRiskInput.cs
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

GitHub webhook ayarında payload URL'sini `/webhooks/github`, content type'ı `application/json` ve secret'i uygulamaya verilen değerle aynı ayarlayın. GitHub her teslimatta gerekli `X-Hub-Signature-256`, `X-GitHub-Delivery` ve `X-GitHub-Event` başlıklarını gönderir. İlk geçerli ve desteklenen teslimatın örnek yanıtı şöyledir:

```json
{
  "deliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
  "eventName": "pull_request",
  "status": "accepted",
  "riskInput": {
    "sourceDeliveryId": "0b989ba4-242f-11e5-81e1-c7b6966d2516",
    "sourceProvider": "github",
    "kind": "change_opened",
    "repository": "acme/ReleaseGuard",
    "changeNumber": 42,
    "title": "Protect production releases",
    "author": "octocat",
    "baseBranch": "main",
    "headBranch": "feature/release-guard",
    "isDraft": false,
    "changedFiles": 4,
    "additions": 120,
    "deletions": 15
  }
}
```

Aynı GUID tekrar gelirse `status` değeri `duplicate` olur. Desteklenmeyen event/action için `status` değeri `ignored` ve `riskInput` değeri `null` olur. Otomatik testler imza hatalarına ek olarak normalizasyon alanlarını, bozuk desteklenen payload'ı, bilinmeyen olayları, JSON ayrıştırmayı ve tekrar teslimatı gerçek HTTP istekleriyle kapsar; manuel HMAC üretimi doğrulama için gerekli değildir.

## Nasıl doğruladık?

Checkpoint aşağıdaki sırayla doğrulanır:

1. Restore ile NuGet bağımlılıklarının çözümlenmesi.
2. Solution build ile warning-as-error dahil tüm projelerin derlenmesi.
3. Gerçek ASP.NET Core test host'u üzerinden sağlık ve webhook senaryolarının çalışması.

Son doğrulamada build `0 uyarı / 0 hata`, testler `15/15 başarılı` sonucunu verdi. Kısıtlı sandbox'ta test runner yerel IPC soketi açmak için ek izin isteyebilir; bu uygulamanın ağ bağımlılığı olduğu anlamına gelmez.

## Sıradaki küçük adım

Bu checkpoint burada durur. Sonraki küçük adımda `ReleaseRiskInput` üzerinden çalışan, yan etkisiz ve açıklanabilir ilk deterministik risk değerlendirmesi tasarlanabilir; örneğin değişen dosya ve satır sayılarını açık eşiklerle sınıflandırıp sonucu gerekçeleriyle döndürebilir. `synchronize` gibi yeni action'lar ancak aynı girdiye hangi güncelleme semantiğiyle bağlanacağı netleştiğinde eklenmelidir. Kalıcı idempotency deposu ise ilk gerçek yan etki veya çoklu-instance çalışma eklenmeden önce benzersiz anahtar ve transaction sınırıyla ele alınmalıdır.
