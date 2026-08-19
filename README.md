# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo şu anda **Checkpoint 2: güvenli GitHub webhook kabul sınırı**na ulaşmıştır. Sağlık kontrolüne ek olarak GitHub'ın `X-Hub-Signature-256` imzasını doğrulayan bir webhook uç noktası vardır. Kafka, PostgreSQL, domain event, Python AI servisi ve dashboard henüz eklenmemiştir.

## Bu adımda ne yapıyoruz?

- `POST /webhooks/github` uç noktasında isteğin ham byte akışını HMAC-SHA256 ile doğruluyoruz.
- Secret'i .NET configuration/options üzerinden alıyor ve başlangıçta doğruluyoruz.
- Eksik, biçimsiz ve geçersiz imzaları ayrı HTTP durumlarıyla reddediyoruz.
- Geçerli ve hatalı akışları gerçek ASP.NET Core test host'u üzerinden doğruluyoruz.

## Neden bu kadar küçük başlıyoruz?

Webhook, sistemin internetten veri alan ilk güven sınırıdır. İmza kontrolünü event üretimi, Kafka ve veritabanından ayrı kurmak; kimlik doğrulama hatalarını altyapı hatalarından ayırır. Bu checkpoint yalnızca isteğin güvenilirliğini doğrular ve kabul eder; payload ayrıştırma veya yayınlama yapmaz.

## Mimari kararlar

### Neden ham gövdeyi doğruluyoruz?

GitHub imzayı gönderdiği byte dizisi üzerinden üretir. JSON önce deserialize edilip yeniden oluşturulursa boşluklar, alan sırası veya karakter kodlaması değişebilir ve doğru imza reddedilebilir. Bu nedenle doğrulayıcı `Request.Body` akışını doğrudan HMAC hesabına verir.

Akış belleğe bütünüyle kopyalanmadığı için payload boyutuyla büyüyen ek bir buffer oluşmaz. Trade-off: akış doğrulama sırasında tüketilir. İleride aynı gövde ayrıştırılacaksa kontrollü buffering veya doğrulama ile ayrıştırmayı birlikte yapan bir pipeline tasarlanmalıdır.

### Neden HMAC-SHA256 ve sabit-zamanlı karşılaştırma?

GitHub'ın `X-Hub-Signature-256` sözleşmesi `sha256=<64 hex karakter>` biçimindedir. Sunucu aynı secret ve ham gövdeyle HMAC-SHA256 üretir. İki digest normal eşitlik operatörüyle değil `CryptographicOperations.FixedTimeEquals` ile karşılaştırılır; böylece eşleşen prefix uzunluğundan bilgi sızdıran timing saldırısı riski azaltılır.

Trade-off: yalnızca GitHub'ın SHA-256 imza şeması kabul edilir; eski `X-Hub-Signature`/SHA-1 başlığı bilinçli olarak desteklenmez.

### Secret neden options/configuration içinde?

Kod yalnızca `GitHubWebhook:Secret` yapılandırma anahtarını bilir; gerçek secret repoya yazılmaz. .NET configuration hiyerarşisi sayesinde yerelde `GitHubWebhook__Secret` ortam değişkeni, üretimde ise ortamın secret manager/configuration provider'ı kullanılabilir. Uygulama secret boşsa veya 32 karakterden kısaysa `ValidateOnStart` ile açılmaz; yanlış yapılandırmayla güvensiz çalışmak yerine fail-fast davranır.

Minimum uzunluk tahmin edilmesi kolay secret riskini azaltır. Trade-off: eski ve kısa bir secret doğrudan kullanılamaz; en az 32 karakterlik yüksek entropili yeni bir değer üretilmelidir. Ortam değişkeni pratik bir yerel geliştirme seçeneğidir; üretimde platformun secret manager'ı tercih edilmelidir.

### HTTP durumları neden böyle?

| Durum | Yanıt | Gerekçe |
| --- | --- | --- |
| İmza geçerli | `202 Accepted` | Kimlik doğrulandı; ileride asenkron işleme eklenmesine uygun kabul semantiği sağlar. |
| Başlık eksik | `401 Unauthorized` | İstek gerekli kimlik doğrulama kanıtını taşımıyor. |
| Şema/uzunluk/hex biçimi bozuk | `400 Bad Request` | İstemci protokol sözleşmesine uymayan bir değer gönderdi. |
| Biçimi doğru fakat digest yanlış | `401 Unauthorized` | Kimlik doğrulama başarısız; ayrıntılı karşılaştırma bilgisi açıklanmaz. |

Geçerli istekte henüz payload işlenmez veya event üretilmez. `202` bu checkpoint'te yalnızca güvenlik sınırının geçildiğini bildirir.

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

Bu turda çözdüğümüz problem yalnızca webhook güvenlik sınırıdır. Kafka ve PostgreSQL yeni çalışma zamanı bağımlılıkları, hata modları ve tasarım kararları getirir. İmza doğrulama testlerle sabitlenmeden bunları eklemek hata kaynağını belirsizleştirirdi.

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
│       ├── GitHubWebhookEndpoint.cs
│       ├── GitHubWebhookOptions.cs
│       └── GitHubWebhookSignatureValidator.cs
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

GitHub webhook ayarında payload URL'sini `/webhooks/github`, content type'ı `application/json` ve secret'i uygulamaya verilen değerle aynı ayarlayın. Uç nokta `X-Hub-Signature-256` başlığını zorunlu tutar. Otomatik testler geçerli, geçersiz, eksik ve biçimsiz başlıkları gerçek HTTP istekleriyle kapsar; manuel HMAC üretimi doğrulama için gerekli değildir.

## Nasıl doğruladık?

Checkpoint aşağıdaki sırayla doğrulanır:

1. Restore ile NuGet bağımlılıklarının çözümlenmesi.
2. Solution build ile warning-as-error dahil tüm projelerin derlenmesi.
3. Gerçek ASP.NET Core test host'u üzerinden sağlık ve webhook senaryolarının çalışması.

Son doğrulamada build `0 uyarı / 0 hata`, testler `6/6 başarılı` sonucunu verdi. Kısıtlı sandbox'ta test runner yerel IPC soketi açmak için ek izin isteyebilir; bu uygulamanın ağ bağımlılığı olduğu anlamına gelmez.

## Sıradaki küçük adım

Bu checkpoint burada durur. Sonraki ayrı adımda doğrulanmış payload için domain sözleşmesi ve idempotency ihtiyacı değerlendirilebilir; Kafka, PostgreSQL veya başka servis bu checkpoint'e dahil değildir.
