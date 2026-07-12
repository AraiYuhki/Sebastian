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
    <h2>現在の設定（<span id="config-name"></span>）</h2>
    <pre id="config">読み込み中…</pre>
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
      $("config").textContent = c.content;
    } catch (e) { $("config").textContent = "設定ファイルを読み込めませんでした。"; }
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
