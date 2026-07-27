namespace SebastianCi.Web;

/// <summary>
/// ダッシュボードの単一ページHTML（外部依存なしの自己完結型）を提供する。
/// </summary>
public static class DashboardPage
{
    public static string Html { get; } = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>sebastian-ci</title>
<style>
  :root { color-scheme: light dark; --bg:#f5f6f8; --card:#fff; --line:#e2e5ea; --fg:#1c2330;
    --muted:#6b7280; --accent:#2563eb; --ok:#16a34a; --ng:#dc2626; --warn:#d97706; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0f141b; --card:#171e29; --line:#26303d;
    --fg:#e6eaf0; --muted:#9aa4b2; } }
  * { box-sizing: border-box; }
  body { margin:0; font-family: system-ui, sans-serif; background:var(--bg); color:var(--fg); }
  header { background:var(--card); border-bottom:1px solid var(--line); padding:14px 20px;
    display:flex; align-items:center; gap:12px; position:sticky; top:0; }
  header h1 { font-size:18px; margin:0; font-weight:700; }
  header .sub { color:var(--muted); font-size:13px; }
  main { max-width:1000px; margin:0 auto; padding:20px; display:grid; gap:20px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; }
  .card h2 { font-size:15px; margin:0 0 14px; }
  .meters { display:grid; grid-template-columns:1fr 1fr; gap:16px; }
  .meter .label { font-size:13px; color:var(--muted); display:flex; justify-content:space-between; }
  .bar { height:10px; background:var(--line); border-radius:6px; overflow:hidden; margin-top:6px; }
  .bar > span { display:block; height:100%; background:var(--accent); }
  table { width:100%; border-collapse:collapse; font-size:14px; }
  th, td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--line); }
  th { color:var(--muted); font-weight:600; font-size:12px; }
  .pill { display:inline-block; padding:2px 9px; border-radius:999px; font-size:12px; font-weight:600; }
  .pill.ok { background:color-mix(in srgb,var(--ok) 18%,transparent); color:var(--ok); }
  .pill.ng { background:color-mix(in srgb,var(--ng) 18%,transparent); color:var(--ng); }
  pre { background:var(--bg); border:1px solid var(--line); border-radius:8px; padding:14px;
    overflow:auto; font-size:13px; margin:0; max-height:360px; }
  .muted { color:var(--muted); font-size:13px; }
  textarea { width:100%; min-height:260px; font-family:ui-monospace,monospace; font-size:13px;
    background:var(--bg); color:var(--fg); border:1px solid var(--line); border-radius:8px; padding:12px; resize:vertical; }
  button { font:inherit; font-weight:600; border:0; border-radius:8px; padding:9px 16px; cursor:pointer;
    background:var(--accent); color:#fff; }
  button.secondary { background:var(--line); color:var(--fg); }
  button:disabled { opacity:.5; cursor:not-allowed; }
  .row { display:flex; gap:10px; align-items:center; margin-top:12px; flex-wrap:wrap; }
  .result { margin-top:12px; padding:10px 12px; border-radius:8px; font-size:13px; white-space:pre-wrap;
    display:none; }
  .result.ok { display:block; background:color-mix(in srgb,var(--ok) 15%,transparent); color:var(--ok); }
  .result.ng { display:block; background:color-mix(in srgb,var(--ng) 15%,transparent); color:var(--ng); }
  .log { background:#0b0e14; color:#d7dde8; border-radius:8px; padding:14px; font-family:ui-monospace,monospace;
    font-size:12.5px; white-space:pre-wrap; max-height:420px; overflow:auto; min-height:80px; }
  label.chk { display:flex; align-items:center; gap:6px; font-size:13px; color:var(--muted); }
  .dot { width:9px; height:9px; border-radius:50%; background:var(--muted); display:inline-block; }
  .dot.run { background:var(--warn); animation:pulse 1s infinite; }
  .dot.done { background:var(--ok); } .dot.fail { background:var(--ng); }
  @keyframes pulse { 50% { opacity:.3; } }
  tbody tr.clickable { cursor:pointer; } tbody tr.clickable:hover { background:color-mix(in srgb,var(--accent) 8%,transparent); }
  .overlay { position:fixed; inset:0; background:rgba(0,0,0,.5); display:none; align-items:center; justify-content:center; padding:20px; }
  .overlay.open { display:flex; }
  .modal { background:var(--card); border:1px solid var(--line); border-radius:12px; max-width:820px; width:100%;
    max-height:85vh; overflow:auto; padding:20px; }
  .modal h3 { margin:0 0 4px; font-size:16px; } .modal .close { float:right; cursor:pointer; color:var(--muted); font-size:20px; }
  .joblist button { margin-left:auto; padding:4px 10px; font-size:12px; }
  .joblist .jobrow { display:flex; align-items:center; gap:10px; padding:8px 0; border-bottom:1px solid var(--line); }
</style>
</head>
<body>
<header>
  <h1>🛠 sebastian-ci</h1>
  <span class="sub" id="repo"></span>
</header>
<main>
  <section class="card">
    <h2>マシンのリソース</h2>
    <div class="meters">
      <div class="meter"><div class="label"><span>メモリ</span><span id="mem-text">—</span></div>
        <div class="bar"><span id="mem-bar"></span></div></div>
      <div class="meter"><div class="label"><span>ディスク</span><span id="disk-text">—</span></div>
        <div class="bar"><span id="disk-bar"></span></div></div>
    </div>
  </section>
  <section class="card">
    <h2>実行履歴</h2>
    <table><thead><tr><th>コミット</th><th>状態</th><th>実行日時</th><th>ジョブ</th></tr></thead>
      <tbody id="history"><tr><td colspan="4" class="muted">読み込み中…</td></tr></tbody></table>
  </section>
  <section class="card">
    <h2>実行</h2>
    <div class="row">
      <button id="run-btn" onclick="startRun()">▶ 実行する</button>
      <label class="chk"><input type="checkbox" id="rebuild"> 実行済みでも再実行（--rebuild）</label>
      <span style="margin-left:auto"><span class="dot" id="run-dot"></span> <span id="run-state" class="muted">待機中</span></span>
    </div>
    <div class="log" id="log" style="margin-top:12px;">まだ実行していません。「実行する」を押すと、ここにログが流れます。</div>
  </section>
  <section class="card">
    <h2>設定の編集（<span id="config-name"></span>）</h2>
    <textarea id="config" spellcheck="false">読み込み中…</textarea>
    <div class="row">
      <button onclick="saveConfig()">💾 保存して検証</button>
      <button class="secondary" onclick="loadConfig()">元に戻す</button>
    </div>
    <div class="result" id="config-result"></div>
  </section>
  <section class="card">
    <h2>プラグイン</h2>
    <p class="muted" style="margin:0 0 12px">NuGet パッケージやローカルの .dll を追加して、通知先やジョブランナーを拡張できます。追加すると設定ファイルに反映され、その場で検証されます。</p>
    <div class="joblist" id="plugin-list"><span class="muted">読み込み中…</span></div>
    <div class="row" style="margin-top:16px">
      <select id="plugin-kind" onchange="switchPluginKind()" style="font:inherit;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--surface);color:var(--fg)">
        <option value="package">NuGet パッケージ</option>
        <option value="path">ローカルの .dll</option>
      </select>
      <input id="plugin-package" placeholder="パッケージID（例: YourOrg.SebastianCi.Teams）" style="flex:1;min-width:180px;font:inherit;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--fg)">
      <input id="plugin-version" placeholder="バージョン（省略可）" style="width:140px;font:inherit;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--fg)">
      <input id="plugin-path" placeholder="パス（例: ./plugins/My.dll）" style="flex:1;min-width:180px;display:none;font:inherit;padding:8px 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg);color:var(--fg)">
      <button id="plugin-add-btn" onclick="addPlugin()">＋ 追加して検証</button>
    </div>
    <div class="result" id="plugin-result"></div>
  </section>
</main>
<div class="overlay" id="overlay" onclick="if(event.target===this)closeModal()">
  <div class="modal">
    <span class="close" onclick="closeModal()">×</span>
    <h3 id="modal-title">実行の詳細</h3>
    <div class="muted" id="modal-sub"></div>
    <div class="joblist" id="modal-jobs" style="margin-top:14px;"></div>
    <pre id="modal-log" style="display:none; margin-top:14px;"></pre>
  </div>
</div>
<script>
  const $ = id => document.getElementById(id);
  async function getJson(url) { const r = await fetch(url); if (!r.ok) throw new Error(url); return r.json(); }

  function renderMeter(prefix, availableMb, totalMb) {
    const usedRatio = totalMb > 0 ? 1 - availableMb / totalMb : 0;
    $(prefix + "-text").textContent = `${availableMb} / ${totalMb} MB 空き`;
    const bar = $(prefix + "-bar");
    bar.style.width = Math.round(usedRatio * 100) + "%";
    bar.style.background = usedRatio > 0.9 ? "var(--ng)" : usedRatio > 0.75 ? "var(--warn)" : "var(--accent)";
  }

  async function loadResources() {
    try {
      const r = await getJson("/api/resources");
      renderMeter("mem", r.availableMemoryMb, r.totalMemoryMb);
      renderMeter("disk", r.availableDiskMb, r.totalDiskMb);
    } catch (e) { /* サーバ未応答時は前回表示を維持 */ }
  }

  function statusPill(s) {
    const ok = s === "Success" || s === "SkippedByChanges" || s === "FailedIgnored";
    return `<span class="pill ${ok ? "ok" : "ng"}">${s}</span>`;
  }

  async function loadHistory() {
    try {
      const rows = await getJson("/api/history");
      if (!rows.length) { $("history").innerHTML = '<tr><td colspan="4" class="muted">まだ実行履歴はありません。</td></tr>'; return; }
      $("history").innerHTML = rows.map(e => {
        const jobs = e.record.jobs.map(j => j.jobId).join(", ");
        const when = new Date(e.record.executedAt).toLocaleString();
        return `<tr class="clickable" onclick="openRun('${e.commitHash}')"><td><code>${e.commitHash.slice(0,8)}</code></td>
          <td>${statusPill(e.record.isSuccess ? "Success" : "Failed")}</td>
          <td>${when}</td><td class="muted">${jobs}</td></tr>`;
      }).join("");
    } catch (e) { /* 維持 */ }
  }

  function closeModal() { $("overlay").classList.remove("open"); }

  async function openRun(commit) {
    $("modal-log").style.display = "none";
    $("modal-jobs").innerHTML = "読み込み中…";
    $("overlay").classList.add("open");
    try {
      const r = await getJson("/api/history/" + commit);
      $("modal-title").textContent = "実行の詳細";
      $("modal-sub").innerHTML = `コミット <code>${commit.slice(0,12)}</code> ・ ${new Date(r.executedAt).toLocaleString()} ・ ` + statusPill(r.isSuccess ? "Success" : "Failed");
      $("modal-jobs").innerHTML = r.jobs.map(j => `
        <div class="jobrow">${statusPill(j.status)}<span>${j.jobId}</span>
          <span class="muted">${j.durationSeconds.toFixed(1)} 秒</span>
          <button onclick='viewLog("${commit}", ${JSON.stringify(j.jobId)})'>ログを見る</button></div>`).join("");
    } catch (e) { $("modal-jobs").textContent = "詳細を取得できませんでした。"; }
  }

  async function viewLog(commit, jobId) {
    const pre = $("modal-log");
    pre.style.display = "block"; pre.textContent = "読み込み中…";
    try {
      const res = await fetch("/api/logs/" + commit + "/" + encodeURIComponent(jobId));
      pre.textContent = res.ok ? (await res.text()) || "(ログは空です)" : "このジョブのログは見つかりませんでした。";
    } catch (e) { pre.textContent = "ログを取得できませんでした。"; }
  }

  async function loadConfig() {
    try {
      const c = await getJson("/api/config");
      $("config-name").textContent = c.fileName;
      $("config").value = c.content;
      const res = $("config-result");
      res.className = "result";
      res.textContent = c.exists === false
        ? "ℹ 設定ファイルはまだ作成されていません。雛形を編集して「保存して検証」を押すと作成されます。"
        : "";
    } catch (e) { $("config").value = "設定ファイルを読み込めませんでした。"; }
  }

  async function saveConfig() {
    const res = $("config-result");
    res.className = "result"; res.textContent = "";
    try {
      const r = await fetch("/api/config", { method:"POST", headers:{"Content-Type":"application/json"},
        body: JSON.stringify({ content: $("config").value }) });
      const data = await r.json();
      res.className = "result " + (data.valid ? "ok" : "ng");
      res.textContent = (data.valid ? "✅ 保存しました。構成は正常です。\n" : "❌ 保存しましたが、構成にエラーがあります。\n") + (data.output || "");
    } catch (e) { res.className = "result ng"; res.textContent = "保存に失敗しました。"; }
  }

  async function startRun() {
    $("run-btn").disabled = true;
    $("log").textContent = "";
    try {
      const r = await fetch("/api/run", { method:"POST", headers:{"Content-Type":"application/json"},
        body: JSON.stringify({ rebuild: $("rebuild").checked }) });
      const data = await r.json();
      if (!data.started) { $("log").textContent = "⚠ " + (data.reason || "開始できませんでした。"); $("run-btn").disabled = false; return; }
      streamRun();
    } catch (e) { $("log").textContent = "実行を開始できませんでした。"; $("run-btn").disabled = false; }
  }

  // WebSocket で実行ログを逐次受信する（接続できない環境ではポーリングにフォールバック）
  function streamRun() {
    const dot = $("run-dot");
    dot.className = "dot run"; $("run-state").textContent = "実行中…";
    let lines = [];
    let ws;
    try { ws = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/api/run/ws"); }
    catch (e) { pollRun(); return; }

    ws.onmessage = ev => {
      const m = JSON.parse(ev.data);
      if (m.line !== undefined) { lines.push(m.line); $("log").textContent = lines.join("\n"); $("log").scrollTop = $("log").scrollHeight; }
      if (m.done) { finishRun(m.exitCode); ws.close(); }
    };
    ws.onerror = () => { try { ws.close(); } catch (e) {} pollRun(); };
  }

  function finishRun(exitCode) {
    const ok = exitCode === 0;
    $("run-dot").className = "dot " + (ok ? "done" : "fail");
    $("run-state").textContent = ok ? "完了（成功）" : `完了（終了コード ${exitCode}）`;
    $("run-btn").disabled = false;
    loadHistory();
  }

  async function pollRun() {
    try {
      const s = await getJson("/api/run");
      $("log").textContent = s.lines.join("\n");
      $("log").scrollTop = $("log").scrollHeight;
      if (s.running) { $("run-dot").className = "dot run"; $("run-state").textContent = "実行中…"; setTimeout(pollRun, 1000); }
      else finishRun(s.exitCode);
    } catch (e) { setTimeout(pollRun, 1500); }
  }

  async function loadMeta() {
    try { const m = await getJson("/api/meta"); $("repo").textContent = m.repositoryPath; } catch (e) {}
  }

  function switchPluginKind() {
    const isPackage = $("plugin-kind").value === "package";
    $("plugin-package").style.display = isPackage ? "" : "none";
    $("plugin-version").style.display = isPackage ? "" : "none";
    $("plugin-path").style.display = isPackage ? "none" : "";
  }

  function pluginLabel(p) {
    if (p.package) return `📦 ${p.package}${p.version ? " @ " + p.version : ""}`;
    return `📁 ${p.path}`;
  }

  async function loadPlugins() {
    try {
      const data = await getJson("/api/plugins");
      renderPlugins(data.plugins || []);
    } catch (e) { $("plugin-list").innerHTML = '<span class="muted">プラグイン情報を取得できませんでした。</span>'; }
  }

  function renderPlugins(plugins) {
    if (!plugins.length) { $("plugin-list").innerHTML = '<span class="muted">プラグインはまだ追加されていません。</span>'; return; }
    $("plugin-list").innerHTML = plugins.map(p => {
      const id = p.package || p.path;
      return `<div class="jobrow"><span>${pluginLabel(p)}</span>
        <button class="secondary" style="margin-left:auto;padding:4px 10px;font-size:12px"
          onclick='removePlugin(${JSON.stringify(id)})'>削除</button></div>`;
    }).join("");
  }

  async function addPlugin() {
    const isPackage = $("plugin-kind").value === "package";
    const body = isPackage
      ? { package: $("plugin-package").value.trim(), version: $("plugin-version").value.trim() }
      : { path: $("plugin-path").value.trim() };
    await mutatePlugins("/api/plugins", body, "追加");
  }

  async function removePlugin(identifier) {
    await mutatePlugins("/api/plugins/remove", { identifier }, "削除");
  }

  async function mutatePlugins(url, body, verb) {
    const res = $("plugin-result");
    res.className = "result"; res.textContent = "";
    $("plugin-add-btn").disabled = true;
    try {
      const r = await fetch(url, { method:"POST", headers:{"Content-Type":"application/json"}, body: JSON.stringify(body) });
      const data = await r.json();
      res.className = "result " + (data.valid ? "ok" : "ng");
      res.textContent = (data.valid ? `✅ ${verb}しました。構成は正常です。\n` : `❌ ${verb}しましたが、検証でエラーになりました。\n`) + (data.output || "");
      if (data.plugins) renderPlugins(data.plugins);
      loadConfig();   // 設定ファイルが書き換わったのでエディタも更新する
    } catch (e) { res.className = "result ng"; res.textContent = `${verb}に失敗しました。`; }
    $("plugin-add-btn").disabled = false;
  }

  loadMeta(); loadConfig(); loadHistory(); loadResources(); loadPlugins();
  setInterval(loadResources, 3000);
  setInterval(loadHistory, 5000);
</script>
</body>
</html>
""";
}
