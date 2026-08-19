# ReleaseGuard AI

ReleaseGuard AI; pull request, commit, CI ve deployment olaylarını işleyip değişiklik riskini açıklanabilir biçimde değerlendirmeyi hedefleyen bir yazılım teslimat platformudur.

Bu depo şu anda yalnızca **Checkpoint 1: çalışır .NET temeli**ni içerir. Ürün davranışı olarak bir sağlık kontrolü vardır; GitHub webhook'u, Kafka, PostgreSQL, Python AI servisi ve dashboard henüz eklenmemiştir.

## Bu adımda ne yapıyoruz?

- Çözüm dosyasını ve ortak .NET derleme ayarlarını oluşturuyoruz.
- İlk servis sınırı olarak `WebhookIngestion.Api` projesini açıyoruz.
- Servisin ayağa kalktığını gösteren `GET /health` uç noktasını ekliyoruz.
- Uç noktayı gerçek test host'u üzerinden çağıran bir entegrasyon testi ekliyoruz.

## Neden bu kadar küçük başlıyoruz?

Webhook imzası, Kafka ve veritabanı aynı anda eklenirse bir başarısızlığın uygulamadan mı yoksa altyapıdan mı kaynaklandığını ayırmak zorlaşır. Bu checkpoint yalnızca .NET derleme ve test döngüsünü kanıtlar. Sonraki özellikler bu çalışan tabanın üstüne, ayrı ve doğrulanabilir adımlarla eklenecektir.

## Başlangıç kararları

### Neden monorepo?

ReleaseGuard'ın .NET servisleri, Python AI servisi, olay sözleşmeleri ve ilerideki dashboard'u aynı ürünün parçalarıdır. Erken aşamada tek depo kullanmak:

- bir olay sözleşmesi değiştiğinde üretici, tüketici ve testleri tek değişiklikte güncellemeyi,
- tek bir yerel geliştirme ve CI giriş noktası sunmayı,
- mimari kararları ve sürüm geçmişini birlikte tutmayı

kolaylaştırır.

Trade-off şudur: depo büyüdükçe tüm projeleri her değişiklikte derlemek pahalılaşabilir ve servis sınırları bulanıklaşabilir. Bunu net klasör/proje sınırları, bağımlılık kuralları ve ileride path-filtered CI ile yöneteceğiz. Takımların yayın döngüleri gerçekten bağımsızlaşırsa bazı bileşenleri ayrı depolara taşımak yeniden değerlendirilebilir.

### Neden şimdilik .NET 8?

Hedef teknoloji .NET 10'dur; ancak incelenen makinede yalnızca .NET SDK `8.0.416` ve .NET runtime `8.0.22` kurulu. Çalışmadığı doğrulanamayan `net10.0` dosyaları üretmek yerine ilk checkpoint `net8.0` ile derlenebilir tutuldu. `global.json` kullanılan SDK'yı sabitler. .NET 10 SDK kurulduğunda hedef framework ayrı bir yükseltme adımında değiştirilecek ve tüm testler yeniden çalıştırılacaktır.

### Neden henüz Kafka veya veritabanı yok?

Bu turda çözdüğümüz problem yalnızca uygulama iskeletidir. Kafka ve PostgreSQL yeni çalışma zamanı bağımlılıkları, hata modları ve tasarım kararları getirir. Webhook kabulü ile güvenlik sınırını kurduktan sonra event sözleşmesini tanımlayıp Kafka'yı gerekçeli biçimde ekleyeceğiz.

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
└── tests/
    └── ReleaseGuard.WebhookIngestion.Api.Tests/
```

Python servisi, altyapı ve dashboard klasörleri ihtiyaç doğduğu checkpoint'lerde oluşturulacaktır; boş yer tutucu klasörler eklenmemiştir.

## Tekrarlanabilir komutlar

Komutları bu README'nin bulunduğu `ReleaseGuard` klasöründe çalıştırın:

```bash
dotnet restore ReleaseGuard.sln
dotnet build ReleaseGuard.sln --no-restore
dotnet test ReleaseGuard.sln --no-build
```

Codex'in kısıtlı sandbox ortamında MSBuild'in çoklu süreç named-pipe iletişimine izin verilmediği için build şu eşdeğer, tek düğümlü komutla doğrulandı:

```bash
dotnet build ReleaseGuard.sln --no-restore --disable-build-servers -m:1
```

Normal yerel terminalde üstteki sade komut yeterlidir; `-m:1` yalnızca paralel MSBuild düğümlerini kapatır ve üretilen kodu değiştirmez.

Servisi çalıştırmak için:

```bash
dotnet run --project src/ReleaseGuard.WebhookIngestion.Api -- --urls http://localhost:5080
curl http://localhost:5080/health
```

Beklenen sağlık yanıtı:

```json
{"status":"ok","service":"webhook-ingestion"}
```

## Sıradaki küçük adım

GitHub webhook endpoint'inin istek gövdesini değiştirmeden yakalaması, `X-Hub-Signature-256` imzasını HMAC-SHA256 ile doğrulaması ve geçersiz imzayı reddetmesi. Bu aşamada Kafka eklenmeyecek; önce güvenlik sınırı birim ve entegrasyon testleriyle sabitlenecektir.
