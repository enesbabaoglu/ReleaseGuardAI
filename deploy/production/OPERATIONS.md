# Tek sunuculu production Docker Compose işletim rehberi

Bu profil küçük ve kontrollü bir ortamda ReleaseGuard'ı tek Linux sunucuda çalıştırmak içindir. Caddy yalnız public giriş noktasıdır; dashboard, webhook API, Keycloak, PostgreSQL, Redpanda, Ollama ve AI servisi host portu yayımlamaz. Public URL'ler şunlardır:

- `https://<domain>/`: Keycloak login korumalı dashboard
- `https://<domain>/identity/`: ReleaseGuard realm OIDC uçları
- `https://<domain>/webhooks/github`: imzalı GitHub webhook

Keycloak admin yolları ile `.NET` read/replay/health yolları Caddy'den public edilmez. Container içindeki `app` ağı external erişime kapalıdır; yalnız Ollama model init işi ayrı egress ağına çıkar.

## 1. Sunucu ve DNS önkoşulları

- Linux üzerinde güncel Docker Engine ve Docker Compose v2 kurulu olmalıdır.
- Domain'in `A` kaydı sunucunun public IPv4 adresine gitmelidir. IPv6 kullanılmıyorsa hatalı bir `AAAA` kaydı bırakılmamalıdır.
- Firewall yalnız inbound `80/tcp`, `443/tcp` ve `443/udp` portlarını açmalıdır. SSH ayrı yönetim politikanıza göre sınırlandırılmalıdır.
- Caddy'nin ACME sertifikası alabilmesi için DNS çözümlemesi ve outbound HTTPS çalışmalıdır.
- Başlangıç kapasitesi olarak en az 4 vCPU, 16 GiB RAM ve yedeklenen SSD depolama önerilir. Ollama latency'si CPU/GPU ve model boyutuna bağlıdır.

Bu tek-host profil **yüksek erişilebilir değildir**. Sunucu, Docker daemon, disk, tek PostgreSQL, tek Redpanda broker, tek Keycloak veya tek dashboard instance arızası kesinti yaratır. Redpanda'nın resmî production önerisi ayrı node'larda en az üç broker ve uygun NVMe/XFS/IOPS'tur; buradaki tek broker/replica seçimi yalnız tek-sunucu kararının açık sonucudur.

## 2. Environment ve secret hazırlığı

Örnek dosyayı kopyalayın ve gerçek domain/e-posta ile düzenleyin:

```bash
cp deploy/production/production.env.example deploy/production/production.env
chmod 600 deploy/production/production.env
```

`RELEASEGUARD_PUBLIC_HOST` yalnız DNS adı olmalıdır; scheme, port veya path içeremez. `releaseguard.example.com` gibi örnek domain validator tarafından reddedilir.

Altı bağımsız secret dosyası üretin. Komutlar secret değerlerini terminale yazmaz:

```bash
install -d -m 700 deploy/production/secrets
openssl rand -hex 32 -out deploy/production/secrets/releaseguard_postgres_password
openssl rand -hex 32 -out deploy/production/secrets/keycloak_postgres_password
openssl rand -hex 32 -out deploy/production/secrets/keycloak_admin_password
openssl rand -hex 32 -out deploy/production/secrets/github_webhook_secret
openssl rand -hex 32 -out deploy/production/secrets/query_credential
openssl rand -hex 32 -out deploy/production/secrets/replay_credential
chmod 600 deploy/production/secrets/*
```

Bu klasörde değer dosyaları git tarafından yok sayılır. Docker Compose bunları yalnız ihtiyaç duyan container'a `/run/secrets/...` regular file olarak bağlar. Bu, değerleri checked-in YAML ve normalize edilmiş `docker compose config` çıktısından uzak tutar; tek başına harici secret manager, HSM, disk encryption veya root erişimine karşı koruma sağlamaz. Sunucu disk encryption ve şifreli/off-host secret yedeği ayrıca kurulmalıdır.

Başlatmadan önce domain, Compose mimarisi ve dosya izinlerini fail-fast doğrulayın:

```bash
node scripts/validate-production-compose.mjs \
  --env-file deploy/production/production.env \
  --check-secrets
```

## 3. Build ve ilk başlangıç

```bash
docker compose \
  --env-file deploy/production/production.env \
  -f compose.production.yml \
  build --pull

docker compose \
  --env-file deploy/production/production.env \
  -f compose.production.yml \
  up --detach --wait
```

İlk başlangıç Ollama modelini indirir; bu işlem ağ ve model boyutuna göre uzun sürebilir. Caddy domain için otomatik TLS sertifikası alır ve HTTP'yi HTTPS'e yönlendirir. Caddy `/data` volume'u ACME account/certificate state'ini korur. Keycloak optimized production image ile açılır, kendi PostgreSQL'ini kullanır ve yalnız boş veritabanında production realm'ini import eder. Realm dosyası mevcut realm'i restart sırasında overwrite etmez.

Durumu ve logları secret basmadan inceleyin:

```bash
docker compose --env-file deploy/production/production.env -f compose.production.yml ps
docker compose --env-file deploy/production/production.env -f compose.production.yml logs --tail=200 caddy keycloak webhook-api dashboard
curl --fail https://YOUR_DOMAIN/
curl --fail https://YOUR_DOMAIN/identity/realms/releaseguard/.well-known/openid-configuration
```

GitHub webhook URL'si `https://YOUR_DOMAIN/webhooks/github` olmalıdır. GitHub'daki webhook secret ile `deploy/production/secrets/github_webhook_secret` birebir aynı değer olmalıdır.

## 4. İlk Keycloak kullanıcısı

Production realm dosyası bilinçli olarak kullanıcı veya parola seed etmez. Stack healthy olduktan sonra interaktif yardımcıyı çalıştırın:

```bash
bash scripts/provision-production-keycloak-user.sh \
  deploy/production/production.env \
  releaseguard-operator
```

Yardımcı kullanıcı adı, doğrulanmış e-posta ve iki kez geçici parola ister; parolayı terminale yazdırmaz. Kullanıcı ilk login'de parolayı değiştirmek zorundadır. Salt-okunur kullanıcı için son argümanı `releaseguard-viewer` yapın. Caddy public Keycloak admin/master yollarını `404` ile kapattığı için yönetim yalnız sunucudaki kontrollü CLI/SSH erişiminden yapılır.

Bootstrap admin kalıcı günlük kullanıcı değildir. İlk kullanıcı/IDP/MFA politikası hazırlandıktan sonra bootstrap admin'i devre dışı bırakmak veya güçlü biçimde rotate etmek, SMTP'yi kurmak, email doğrulama ve MFA politikasını organizasyon standardınıza göre tamamlamak operatör sorumluluğudur.

## 5. Sağlık ve güvenlik kontrolleri

```bash
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T redpanda \
  rpk cluster config get write_caching_default -X admin.hosts=redpanda:9644
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T redpanda \
  rpk cluster config get auto_create_topics_enabled -X admin.hosts=redpanda:9644
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T redpanda \
  rpk topic describe releaseguard.release-risk-assessed -X brokers=redpanda:9092
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T postgres \
  psql -U releaseguard -d releaseguard -c 'SELECT max(version) FROM release_guard_schema_migrations;'
```

Beklenen Redpanda değerleri `write_caching_default=disabled`, `auto_create_topics_enabled=false`, bir partition ve bir replica'dır. Şema sürümü `8` olmalıdır. `docker compose config` yalnız Caddy'de `80/443` published port göstermelidir.

Container logları `json-file` rotasyonu ile bounded tutulur. `.NET` OTLP export bu profilde hedef collector seçilmediği için fail-closed kapalıdır. Host disk/RAM/CPU, PostgreSQL, Redpanda lag/disk, Caddy 5xx/TLS, Keycloak login failure ve uygulama outcome metrikleri için gerçek bir monitoring/alarm hedefi ayrıca bağlanmadan bu profil tam operasyonel gözlemlenebilirlik sağlamaz.

## 6. Tutarlı backup

Backup'tan önce yeni trafik kesilmeli ve durable handoff'un tamamlandığı doğrulanmalıdır:

1. `caddy` servisini durdurun; GitHub tarafında delivery retry devam edebilir.
2. `release_risk_outbox_messages` içinde `published_at IS NULL` sayısı sıfır ve consumer lag sıfır olana kadar bekleyin.
3. `webhook-api`, `dashboard` ve `keycloak` servislerini durdurun. Böylece iki veritabanı dump sırasında uygulama tarafından değiştirilmez.
4. İki PostgreSQL veritabanını `custom`, `--no-owner`, `--no-acl` formatında şifreli/off-host backup alanına alın.
5. Environment/secret dosyalarını ayrı bir şifreli secret kasasına; Caddy data volume'unu sertifika yeniden-issue riskine karşı platform snapshot'ına alın.
6. Servisleri tekrar `up --detach --wait` ile açın ve health kontrolü yapın.

Örnek dump komutları (hedef klasör önceden `0700` olmalıdır):

```bash
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T postgres \
  pg_dump -U releaseguard -d releaseguard --format=custom --no-owner --no-acl > BACKUP_DIR/releaseguard.dump
docker compose --env-file deploy/production/production.env -f compose.production.yml exec -T keycloak-postgres \
  pg_dump -U keycloak -d keycloak --format=custom --no-owner --no-acl > BACKUP_DIR/keycloak.dump
```

Ollama model blob'u yeniden indirilebilir. Redpanda volume'u tek başına uygulamanın canonical backup'ı değildir: bu projede PostgreSQL delivery/outbox/inbox/immutable AI state'i durable kayıt kaynağıdır. Yine de DB dump'tan önce unpublished outbox ve consumer lag sıfır değilse yalnız DB dump almak mesaj kaybı/tekrar penceresi yaratır. Broker volume snapshot'ı gerekiyorsa broker durdurulmalı ve storage sağlayıcısının crash-consistent volume snapshot prosedürü kullanılmalıdır.

## 7. Restore provası

Restore doğrudan canlı volume üzerinde denenmemelidir. Önce izole staging hostta aynı commit, aynı image tag'leri ve yeni boş volume'larla prova edin:

1. Şifreli kasadan environment/secret dosyalarını yükleyin ve permission validator'ı çalıştırın.
2. Yalnız `postgres` ve `keycloak-postgres` servislerini başlatın.
3. `pg_restore --clean --if-exists --no-owner --no-acl` ile iki dump'ı doğru veritabanına geri yükleyin.
4. Kalan servisleri başlatın. Keycloak mevcut realm'i overwrite etmez; .NET şema V008'i doğrular.
5. Şema sürümü, realm/user/roller, signed webhook, outbox→inbox→AI, dashboard login/list/detail ve replay'i doğrulayın.
6. Ölçülen RPO/RTO'yu ve prova tarihini runbook kaydına yazın.

## 8. Upgrade ve rollback

- Önce backup + restore provası alın; release commit'i, image digest'leri ve migration sürümünü kaydedin.
- `docker compose pull` ve `build --pull` sonrasında image taraması/SBOM politikanızı uygulayın.
- Aynı production validator, tüm testler ve staging smoke/E2E geçmeden canlıya çıkmayın.
- Tek-host Compose rolling/zero-downtime sağlamaz. Maintenance penceresinde Caddy'yi durdurun, handoff/lag'i boşaltın, sonra `up --detach --wait` ile yeni image'ları açın.
- V008 sonrası yeni binary eski şemayla başlamaz. Rollback'in DB migration uyumluluğu ayrıca doğrulanmalıdır; yalnız image tag'ini geri almak güvenli rollback garantisi değildir.
- Keycloak major/minor upgrade'i için resmî upgrading guide ve desteklenen PostgreSQL matrisi izlenmelidir. Bir production DB üzerinde eski ve yeni Keycloak sürümleri dönüşümlü denenmemelidir.

## Bilinçli kalan sınırlar

- Tek sunucu ve tek replica nedeniyle HA, autoscaling ve failure-domain izolasyonu yoktur.
- Dashboard session store process belleğindedir; restart kullanıcıyı logout eder. Tek dashboard instance kararında sticky/shared store gerekmez, ikinci instance eklenirse gerekir.
- Query/replay fixed-window limitleri instance belleğindedir; tek instance'ta bounded'dır fakat instance sayısı artırılırsa deployment-wide toplam kota garanti etmez.
- Kafka ve PostgreSQL Docker bridge üzerinde hosta kapalı plaintext kullanır. Bu profil tek güvenilen host/container ağı tehdit modeline dayanır; ayrı node/tenant veya düşman container modelinde TLS/SASL/mTLS gerekir.
- Runtime PostgreSQL kullanıcısı startup migration için DDL yetkilidir. Ayrı tek-seferlik migration identity'si ve daha dar runtime role bu profil içinde henüz ayrılmamıştır.
- Secret rotation single-instance recreate gerektirir; kesintisiz çok-instance rotation/secret manager entegrasyonu yoktur.
- Monitoring backend, paging/on-call, SLO, harici log sink, WAF/DDoS, MFA zorunluluğu, SMTP ve otomatik off-host backup bu Compose dosyası tarafından kurulmaz.
