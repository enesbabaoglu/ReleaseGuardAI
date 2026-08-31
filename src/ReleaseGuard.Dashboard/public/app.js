const elements = {
  refreshButton: document.querySelector("#refresh-button"),
  loadMoreButton: document.querySelector("#load-more-button"),
  searchInput: document.querySelector("#search-input"),
  statusFilter: document.querySelector("#status-filter"),
  table: document.querySelector("#risk-table"),
  listMessage: document.querySelector("#list-message"),
  totalCount: document.querySelector("#total-count"),
  completedCount: document.querySelector("#completed-count"),
  pendingCount: document.querySelector("#pending-count"),
  failedCount: document.querySelector("#failed-count"),
  successRate: document.querySelector("#success-rate"),
  connectionLabel: document.querySelector("#connection-label"),
  detailStatus: document.querySelector("#detail-status"),
  detailTitle: document.querySelector("#detail-title"),
  detailKind: document.querySelector("#detail-kind"),
  detailTime: document.querySelector("#detail-time"),
  detailSummary: document.querySelector("#detail-summary"),
  detailRecommendations: document.querySelector("#detail-recommendations"),
  detailProvider: document.querySelector("#detail-provider"),
  detailModel: document.querySelector("#detail-model"),
  detailEventId: document.querySelector("#detail-event-id"),
  failureBox: document.querySelector("#failure-box"),
  failureReason: document.querySelector("#failure-reason"),
  replayButton: document.querySelector("#replay-button"),
  failureRoleMessage: document.querySelector("#failure-role-message"),
  accountName: document.querySelector("#account-name"),
  accountRole: document.querySelector("#account-role"),
  logoutButton: document.querySelector("#logout-button"),
  toast: document.querySelector("#toast"),
};

const state = {
  items: [],
  nextCursor: null,
  selectedEventId: null,
  aiProvider: "ollama",
  aiModel: "qwen3:1.7b",
  toastTimer: null,
  authEnabled: false,
  canView: false,
  canReplay: false,
  csrfToken: null,
};

const statusLabels = new Map([
  ["completed", "Tamamlandı"],
  ["pending", "Bekliyor"],
  ["failed", "Başarısız"],
]);

const kindLabels = new Map([
  ["change_opened", "Değişiklik açıldı"],
  ["change_updated", "Değişiklik güncellendi"],
]);

function createTextElement(tag, text, className) {
  const element = document.createElement(tag);
  element.textContent = text;
  if (className) {
    element.className = className;
  }
  return element;
}

function renderList() {
  const query = elements.searchInput.value.trim().toLocaleLowerCase("tr");
  const status = elements.statusFilter.value;
  const visibleItems = state.items.filter((item) => {
    const matchesQuery = !query ||
      item.repository.toLocaleLowerCase("tr").includes(query) ||
      String(item.changeNumber).includes(query) ||
      item.eventId.toLocaleLowerCase("tr").includes(query);
    return matchesQuery && (status === "all" || item.status === status);
  });

  const rows = visibleItems.map((item) => {
    const row = document.createElement("tr");
    row.dataset.eventId = item.eventId;
    row.tabIndex = 0;
    if (item.eventId === state.selectedEventId) {
      row.classList.add("selected");
    }
    row.addEventListener("click", () => selectItem(item));
    row.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectItem(item);
      }
    });

    const repositoryCell = document.createElement("td");
    repositoryCell.append(
      createTextElement("strong", item.repository),
      createTextElement("small", `#${item.changeNumber} · ${shortId(item.eventId)}`),
    );
    const kindCell = createTextElement("td", kindLabels.get(item.kind) ?? item.kind);
    const statusCell = document.createElement("td");
    statusCell.append(createTextElement("span", statusLabels.get(item.status) ?? item.status, `status ${item.status}`));
    const acceptedCell = createTextElement("td", formatRelativeTime(item.acceptedAt));
    row.append(repositoryCell, kindCell, statusCell, acceptedCell);
    return row;
  });

  if (rows.length === 0) {
    const row = document.createElement("tr");
    row.className = "loading-row";
    const cell = createTextElement("td", state.items.length === 0 ? "Henüz kabul edilmiş risk olayı yok." : "Bu filtreyle eşleşen kayıt yok.");
    cell.colSpan = 4;
    row.append(cell);
    rows.push(row);
  }
  elements.table.replaceChildren(...rows);
  elements.listMessage.textContent = `${visibleItems.length} / ${state.items.length} kayıt gösteriliyor.`;
}

function renderMetrics() {
  const counts = { completed: 0, pending: 0, failed: 0 };
  for (const item of state.items) {
    if (Object.hasOwn(counts, item.status)) {
      counts[item.status] += 1;
    }
  }
  elements.totalCount.textContent = String(state.items.length);
  elements.completedCount.textContent = String(counts.completed);
  elements.pendingCount.textContent = String(counts.pending);
  elements.failedCount.textContent = String(counts.failed);
  elements.successRate.textContent = state.items.length === 0
    ? "henüz veri yok"
    : `%${Math.round((counts.completed / state.items.length) * 100)} tamamlandı`;
}

async function refreshList({ append = false } = {}) {
  setBusy(elements.refreshButton, true, "Yenileniyor…");
  setBusy(elements.loadMoreButton, true, "Yükleniyor…");
  try {
    const query = new URLSearchParams({ limit: "50" });
    if (append && state.nextCursor) {
      query.set("cursor", state.nextCursor);
    }
    const response = await requestJson(`/api/explanations?${query}`);
    if (!response.body || !Array.isArray(response.body.items)) {
      throw new Error("Liste yanıtı beklenen sözleşmeye uymuyor.");
    }
    const items = response.body.items.filter(isListItem);
    state.items = append ? [...state.items, ...items] : items;
    state.nextCursor = typeof response.body.nextCursor === "string" ? response.body.nextCursor : null;
    elements.loadMoreButton.hidden = state.nextCursor === null;
    renderMetrics();
    renderList();

    if (!append && state.items.length > 0) {
      const selected = state.items.find((item) => item.eventId === state.selectedEventId) ?? state.items[0];
      await selectItem(selected);
    }
  } catch (error) {
    elements.listMessage.textContent = error.message;
    showToast(error.message, true);
  } finally {
    setBusy(elements.refreshButton, false, "↻ Verileri yenile");
    setBusy(elements.loadMoreButton, false, "Daha eski kayıtları getir");
  }
}

async function refreshStatus() {
  try {
    const { body } = await requestJson("/api/status");
    setSignal("api-signal", body?.services?.api);
    setSignal("ai-signal", body?.services?.ai);
    setSignal("ollama-signal", body?.services?.ollama);
    state.aiProvider = typeof body?.ai?.provider === "string" ? body.ai.provider : "ollama";
    state.aiModel = typeof body?.ai?.model === "string" ? body.ai.model : "bilinmiyor";
    elements.detailProvider.textContent = state.aiProvider;
    elements.detailModel.textContent = state.aiModel;
    const allOnline = [body?.services?.api, body?.services?.ai, body?.services?.ollama].every((value) => value === "online");
    elements.connectionLabel.textContent = allOnline && body?.ai?.modelReady ? "CANLI" : "KISMİ";
  } catch {
    for (const id of ["api-signal", "ai-signal", "ollama-signal"]) {
      setSignal(id, "offline");
    }
    elements.connectionLabel.textContent = "BAĞLANTI YOK";
  }
}

async function selectItem(item) {
  state.selectedEventId = item.eventId;
  renderList();
  elements.detailTitle.textContent = `${item.repository} #${item.changeNumber}`;
  elements.detailKind.textContent = kindLabels.get(item.kind) ?? item.kind;
  elements.detailTime.textContent = `${formatDate(item.acceptedAt)} tarihinde kabul edildi.`;
  elements.detailEventId.textContent = item.eventId;
  setDetailStatus(item.status);
  elements.detailSummary.textContent = "Açıklama durumu okunuyor…";
  elements.detailRecommendations.replaceChildren(createTextElement("li", "Lütfen bekleyin."));
  elements.failureBox.hidden = true;

  try {
    const { body } = await requestJson(`/api/events/${encodeURIComponent(item.eventId)}/explanation`);
    if (state.selectedEventId !== item.eventId) {
      return;
    }
    setDetailStatus(body.status);
    if (body.status === "completed" && isExplanation(body.explanation)) {
      elements.detailSummary.textContent = body.explanation.summary;
      elements.detailRecommendations.replaceChildren(
        ...body.explanation.recommendations.map((recommendation) => createTextElement("li", recommendation)),
      );
      return;
    }
    if (body.status === "failed" && body.failure && typeof body.failure.reason === "string") {
      elements.detailSummary.textContent = "AI açıklaması terminal bir hatayla tamamlanamadı.";
      elements.detailRecommendations.replaceChildren(createTextElement("li", "Hata giderildikten sonra aynı event için idempotent replay başlatabilirsiniz."));
      elements.failureReason.textContent = `${body.failure.code ?? "provider_failure"}: ${body.failure.reason}`;
      elements.replayButton.hidden = !state.canReplay;
      elements.failureRoleMessage.hidden = state.canReplay;
      elements.failureBox.hidden = false;
      return;
    }
    elements.detailSummary.textContent = "AI açıklaması sırada; worker sonucu immutable olarak kaydedecek.";
    elements.detailRecommendations.replaceChildren(createTextElement("li", "İşleme tamamlandığında verileri yeniden yükleyin."));
  } catch (error) {
    elements.detailSummary.textContent = error.message;
    elements.detailRecommendations.replaceChildren(createTextElement("li", "Bağlantıyı ve servis durumlarını kontrol edin."));
  }
}

async function requestReplay() {
  if (!state.selectedEventId || !state.canReplay) {
    return;
  }
  setBusy(elements.replayButton, true, "Replay kaydediliyor…");
  try {
    await requestJson(`/api/events/${encodeURIComponent(state.selectedEventId)}/replays`, {
      method: "POST",
      headers: {
        "Idempotency-Key": crypto.randomUUID(),
        ...(state.csrfToken ? { "X-CSRF-Token": state.csrfToken } : {}),
      },
    });
    showToast("Replay isteği kabul edildi; sonuç immutable yeni generation olarak yazılacak.");
    await refreshList();
  } catch (error) {
    showToast(error.message, true);
  } finally {
    setBusy(elements.replayButton, false, "Güvenli replay başlat");
  }
}

async function requestJson(url, options) {
  const response = await fetch(url, { ...options, headers: { Accept: "application/json", ...options?.headers } });
  let body = null;
  try {
    body = await response.json();
  } catch {
    throw new Error("Sunucu geçerli JSON döndürmedi.");
  }
  if (!response.ok) {
    if (response.status === 401 && url !== "/api/session") {
      window.location.assign("/login");
    }
    const retryAfter = response.headers.get("Retry-After");
    const detail = typeof body?.detail === "string" ? body.detail : "İstek tamamlanamadı.";
    throw new Error(retryAfter ? `${detail} ${retryAfter} saniye sonra yeniden deneyin.` : detail);
  }
  return { body, response };
}

async function loadSession() {
  let body;
  try {
    ({ body } = await requestJson("/api/session"));
  } catch (error) {
    window.location.assign("/login");
    throw error;
  }
  state.authEnabled = body?.authEnabled === true;
  state.canView = body?.canView === true;
  state.canReplay = body?.canReplay === true;
  state.csrfToken = typeof body?.csrfToken === "string" ? body.csrfToken : null;
  elements.accountName.textContent = typeof body?.user?.displayName === "string" ? body.user.displayName : "ReleaseGuard kullanıcısı";
  elements.accountRole.textContent = state.authEnabled
    ? state.canReplay ? "OPERATOR" : state.canView ? "VIEWER" : "YETKİSİZ"
    : "YEREL MOD";
  elements.logoutButton.hidden = !state.authEnabled;
}

async function logout() {
  if (!state.authEnabled || !state.csrfToken) return;
  setBusy(elements.logoutButton, true, "Çıkılıyor…");
  try {
    const { body } = await requestJson("/api/logout", {
      method: "POST",
      headers: { "X-CSRF-Token": state.csrfToken },
    });
    window.location.assign(body.redirectTo);
  } catch (error) {
    showToast(error.message, true);
    setBusy(elements.logoutButton, false, "Çıkış");
  }
}

function isListItem(item) {
  return item && typeof item.eventId === "string" && typeof item.status === "string" &&
    typeof item.acceptedAt === "string" && typeof item.repository === "string" &&
    Number.isSafeInteger(item.changeNumber) && typeof item.kind === "string";
}

function isExplanation(explanation) {
  return explanation && typeof explanation.summary === "string" &&
    Array.isArray(explanation.recommendations) &&
    explanation.recommendations.every((value) => typeof value === "string");
}

function setDetailStatus(status) {
  elements.detailStatus.className = `status ${statusLabels.has(status) ? status : "neutral"}`;
  elements.detailStatus.textContent = statusLabels.get(status) ?? "Bilinmiyor";
}

function setSignal(id, status) {
  const signal = document.querySelector(`#${id}`);
  signal.className = `signal ${status === "online" ? "online" : status === "degraded" ? "degraded" : "offline"}`;
}

function setBusy(button, busy, text) {
  button.disabled = busy;
  button.textContent = text;
}

function showToast(message, error = false) {
  window.clearTimeout(state.toastTimer);
  elements.toast.textContent = message;
  elements.toast.className = `toast${error ? " error" : ""}`;
  elements.toast.hidden = false;
  state.toastTimer = window.setTimeout(() => {
    elements.toast.hidden = true;
  }, 5000);
}

function shortId(value) {
  return `${value.slice(0, 8)}…${value.slice(-4)}`;
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function formatRelativeTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) {
    return value;
  }
  const seconds = Math.round((date.valueOf() - Date.now()) / 1000);
  const absolute = Math.abs(seconds);
  const formatter = new Intl.RelativeTimeFormat("tr", { numeric: "auto" });
  if (absolute < 60) return formatter.format(seconds, "second");
  if (absolute < 3600) return formatter.format(Math.round(seconds / 60), "minute");
  if (absolute < 86400) return formatter.format(Math.round(seconds / 3600), "hour");
  return formatter.format(Math.round(seconds / 86400), "day");
}

elements.refreshButton.addEventListener("click", async () => {
  await Promise.all([refreshStatus(), refreshList()]);
});
elements.loadMoreButton.addEventListener("click", () => refreshList({ append: true }));
elements.searchInput.addEventListener("input", renderList);
elements.statusFilter.addEventListener("change", renderList);
elements.replayButton.addEventListener("click", requestReplay);
elements.logoutButton.addEventListener("click", logout);

await loadSession();
await Promise.all([refreshStatus(), refreshList()]);
