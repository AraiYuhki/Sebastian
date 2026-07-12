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
</main>
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
        return `<tr><td><code>${e.commitHash.slice(0,8)}</code></td>
          <td>${statusPill(e.record.isSuccess ? "Success" : "Failed")}</td>
          <td>${when}</td><td class="muted">${jobs}</td></tr>`;
      }).join("");
    } catch (e) { /* 維持 */ }
  }

  async function loadConfig() {
    try {
      const c = await getJson("/api/config");
      $("config-name").textContent = c.fileName;
      $("config").value = c.content;
      $("config-result").className = "result";
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

  loadMeta(); loadConfig(); loadHistory(); loadResources();
  setInterval(loadResources, 3000);
  setInterval(loadHistory, 5000);
</script>
</body>
</html>
""";
}
