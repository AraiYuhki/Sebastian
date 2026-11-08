namespace SebastianCi.Web;

/// <summary>
/// ハブ（複数リポジトリ横断ダッシュボード）の単一ページHTML。外部依存なしの自己完結型。
/// 動的な値はすべて escapeHtml を通し、ボタンへの値渡しは data 属性で行う（インライン JS に文字列を埋め込まない）。
/// </summary>
public static class HubPage
{
    public static string Html { get; } = """
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>sebastian-ci ハブ</title>
<style>
  :root { color-scheme: light dark; --bg:#f5f6f8; --card:#fff; --line:#e2e5ea; --fg:#1c2330;
    --muted:#6b7280; --accent:#2563eb; --ok:#16a34a; --ng:#dc2626; --warn:#d97706; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0f141b; --card:#171e29; --line:#26303d;
    --fg:#e6eaf0; --muted:#9aa4b2; } }
  * { box-sizing: border-box; }
  body { margin:0; font-family: system-ui, sans-serif; background:var(--bg); color:var(--fg); }
  header { background:var(--card); border-bottom:1px solid var(--line); padding:14px 20px;
    display:flex; align-items:center; gap:12px; position:sticky; top:0; z-index:5; }
  header h1 { font-size:18px; margin:0; font-weight:700; }
  #whoami-box { margin-left:auto; display:flex; gap:10px; align-items:center; font-size:13px; }
  #whoami-box form { margin:0; }
  main { max-width:1100px; margin:0 auto; padding:20px; display:grid; gap:20px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:18px; }
  .card h2 { font-size:15px; margin:0 0 14px; display:flex; align-items:center; gap:10px; }
  .card h2 .grow { flex:1; }
  .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; }
  .tile { background:var(--bg); border:1px solid var(--line); border-radius:10px; padding:12px 14px; }
  .tile .n { font-size:26px; font-weight:700; } .tile .l { color:var(--muted); font-size:12px; }
  .tile.ng .n { color:var(--ng); } .tile.run .n { color:var(--warn); }
  table { width:100%; border-collapse:collapse; font-size:14px; }
  th, td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:middle; }
  th { color:var(--muted); font-weight:600; font-size:12px; }
  td.actions { white-space:nowrap; text-align:right; }
  a { color:var(--accent); } a.name { font-weight:600; text-decoration:none; } a.name:hover { text-decoration:underline; }
  .muted { color:var(--muted); font-size:13px; }
  .pill { display:inline-block; padding:2px 9px; border-radius:999px; font-size:12px; font-weight:600; }
  .pill.ok { background:color-mix(in srgb,var(--ok) 18%,transparent); color:var(--ok); }
  .pill.ng { background:color-mix(in srgb,var(--ng) 18%,transparent); color:var(--ng); }
  .pill.run { background:color-mix(in srgb,var(--warn) 20%,transparent); color:var(--warn); }
  .pill.none { background:var(--line); color:var(--muted); }
  .dot { width:9px; height:9px; border-radius:50%; background:var(--muted); display:inline-block; margin-right:6px; }
  .dot.run { background:var(--warn); animation:pulse 1s infinite; } .dot.ok { background:var(--ok); } .dot.ng { background:var(--ng); }
  @keyframes pulse { 50% { opacity:.3; } }
  button { font:inherit; font-weight:600; border:0; border-radius:8px; padding:7px 13px; cursor:pointer; background:var(--accent); color:#fff; }
  button.secondary { background:var(--line); color:var(--fg); }
  button.danger { background:color-mix(in srgb,var(--ng) 18%,transparent); color:var(--ng); }
  button.small { padding:4px 10px; font-size:12px; }
  button:disabled { opacity:.5; cursor:not-allowed; }
  .err { color:var(--ng); font-size:12px; margin-top:3px; }
  textarea { font:inherit; padding:8px 10px; border:1px solid var(--line); border-radius:8px; background:var(--bg); color:var(--fg); resize:vertical; }
  input, select { font:inherit; padding:8px 10px; border:1px solid var(--line); border-radius:8px; background:var(--bg); color:var(--fg); }
  .overlay { position:fixed; inset:0; background:rgba(0,0,0,.5); display:none; align-items:center; justify-content:center; padding:20px; z-index:10; }
  .overlay.open { display:flex; }
  .modal { background:var(--card); border:1px solid var(--line); border-radius:12px; max-width:560px; width:100%; padding:20px; }
  .modal h3 { margin:0 0 12px; font-size:16px; }
  .form-grid { display:grid; gap:12px; }
  .form-grid label { display:flex; flex-direction:column; gap:5px; font-size:13px; color:var(--muted); }
  .form-grid label.radio { flex-direction:row; align-items:center; gap:8px; color:var(--fg); }
  .row { display:flex; gap:10px; align-items:center; margin-top:14px; flex-wrap:wrap; }
  .result { margin-top:12px; padding:10px 12px; border-radius:8px; font-size:13px; white-space:pre-wrap; display:none; }
  .result.ok { display:block; background:color-mix(in srgb,var(--ok) 15%,transparent); color:var(--ok); }
  .result.ng { display:block; background:color-mix(in srgb,var(--ng) 15%,transparent); color:var(--ng); }
  #toast { position:fixed; right:20px; bottom:20px; max-width:360px; padding:10px 14px; border-radius:8px; font-size:13px;
    background:var(--card); border:1px solid var(--line); box-shadow:0 4px 18px rgba(0,0,0,.25); display:none; z-index:20; white-space:pre-wrap; }
  .modal.wide { max-width:760px; }
  .src-row { display:grid; grid-template-columns:2fr 1fr 1fr auto; gap:6px; margin-bottom:6px; }
  .src-row input { min-width:0; }
  .src-head { display:grid; grid-template-columns:2fr 1fr 1fr auto; gap:6px; font-size:12px; color:var(--muted); margin-bottom:4px; }
  .role-user .admin-only { display:none !important; }
  .scroll { overflow-x:auto; }
</style>
</head>
<body>
<header>
  <h1>🛠 sebastian-ci ハブ</h1>
  <span id="whoami-box"><span id="whoami" class="muted"></span>
    <form id="logout-form" method="post" action="/logout" style="display:none"><button class="secondary small" type="submit">ログアウト</button></form></span>
</header>
<main>
  <section class="card">
    <div class="tiles">
      <div class="tile"><div class="n" id="t-repos">–</div><div class="l">登録（リポジトリ・ジョブ）</div></div>
      <div class="tile run"><div class="n" id="t-running">–</div><div class="l">実行中</div></div>
      <div class="tile ng"><div class="n" id="t-failing">–</div><div class="l">直近の実行が失敗</div></div>
      <div class="tile"><div class="n" id="t-jobs">–</div><div class="l">ジョブ（全体）</div></div>
    </div>
  </section>

  <section class="card">
    <h2><span class="grow">リポジトリ・ジョブ</span>
      <button class="admin-only secondary" data-act="open-add">＋ リポジトリを追加</button>
      <button class="admin-only" data-act="open-job">＋ ジョブを作成</button></h2>
    <div class="scroll"><table>
      <thead><tr><th>名前</th><th>直近の実行</th><th>パイプラインのジョブ</th><th>次回のスケジュール</th><th></th></tr></thead>
      <tbody id="repos"><tr><td colspan="5" class="muted">読み込み中…</td></tr></tbody>
    </table></div>
  </section>

  <section class="card">
    <h2><span class="grow">連鎖（ジョブ間のトリガー）</span>
      <button class="admin-only" data-act="open-chain">＋ 連鎖を追加</button></h2>
    <div class="scroll"><table>
      <thead><tr><th>起動元</th><th>条件</th><th>起動先</th><th>直近の動き</th><th></th></tr></thead>
      <tbody id="chains"><tr><td colspan="5" class="muted">読み込み中…</td></tr></tbody>
    </table></div>
    <p class="muted" style="margin-bottom:0">起動元の実行が終わったとき、条件に合えば起動先を自動で実行します。起動先が実行中なら、終わるのを待って実行します（待機は再起動で消えます）。
      「実行済みでスキップ」した実行は、成功にも失敗にも数えず連鎖しません。</p>
  </section>

  <section class="card">
    <h2><span class="grow">すべてのジョブ</span>
      <input id="job-filter" placeholder="絞り込み（リポジトリ名・ジョブ名）" style="width:260px"></h2>
    <div class="scroll"><table>
      <thead><tr><th>リポジトリ</th><th>ジョブ</th><th>ステージ</th><th>前回の結果</th><th></th></tr></thead>
      <tbody id="jobs"><tr><td colspan="5" class="muted">読み込み中…</td></tr></tbody>
    </table></div>
  </section>

  <section class="card">
    <h2>最近の実行（全リポジトリ）</h2>
    <div class="scroll"><table>
      <thead><tr><th>日時</th><th>リポジトリ</th><th>コミット</th><th>結果</th><th>所要時間</th><th>失敗したジョブ</th></tr></thead>
      <tbody id="runs"><tr><td colspan="6" class="muted">読み込み中…</td></tr></tbody>
    </table></div>
  </section>
</main>

<div class="overlay" id="add-overlay"><div class="modal">
  <h3>リポジトリを追加</h3>
  <div class="form-grid">
    <label>名前<input id="add-name" maxlength="60" placeholder="例: my-game"></label>
    <label class="radio"><input type="radio" name="add-kind" value="path" checked> このマシン上の既存リポジトリを登録</label>
    <label id="add-path-label">パス（Git リポジトリのルート）<input id="add-path" placeholder="例: D:\Projects\my-game または /srv/repos/my-game"></label>
    <label class="radio"><input type="radio" name="add-kind" value="url"> Git の URL からクローンして登録</label>
    <label id="add-url-label" style="display:none">Git の URL（https:// / ssh:// / git@host:path）<input id="add-url" placeholder="例: https://github.com/org/repo.git"></label>
    <label>設定ファイル名<input id="add-config" value=".sebastian-ci.yaml"></label>
  </div>
  <p class="muted">クローン先はハブの保存先の workspaces 配下です。認証が必要な場合は、このマシンの git の認証情報や SSH 鍵が使われます（URL にパスワードやトークンは含められません）。</p>
  <div class="row"><button data-act="add-submit" id="add-submit">追加</button>
    <button class="secondary" data-act="close-modal">キャンセル</button></div>
  <div class="result" id="add-result"></div>
</div></div>

<div class="overlay" id="job-overlay"><div class="modal wide">
  <h3 id="job-title">ジョブを作成</h3>
  <div class="form-grid">
    <label>名前<input id="job-name" maxlength="60" placeholder="例: 全体ビルド"></label>
    <div>
      <div class="muted" style="margin-bottom:6px">ソース（Git リポジトリ）。実行のたびに、ジョブの作業場へ取得（clone / 更新）されます。0 件でも作成できます。</div>
      <div class="src-head"><span>Git の URL</span><span>ブランチ（省略可）</span><span>配置先（省略可）</span><span></span></div>
      <div id="job-sources"></div>
      <button class="small secondary" data-act="src-add" type="button">＋ ソースを追加</button>
    </div>
  </div>
  <p class="muted">パイプラインの定義はハブ側（ジョブ専用の定義ファイル）に保存され、リポジトリには何も書き込みません。作成後の詳細画面で編集します。
    配置先を省略すると URL のリポジトリ名になります。認証が必要な場合は、このマシンの git の認証情報や SSH 鍵が使われます。</p>
  <div class="row"><button data-act="job-submit" id="job-submit">作成</button>
    <button class="secondary" data-act="close-modal">キャンセル</button></div>
  <div class="result" id="job-result"></div>
</div></div>

<div class="overlay" id="chain-overlay"><div class="modal">
  <h3 id="chain-title">連鎖を追加</h3>
  <div class="form-grid">
    <label>起動元（これが終わったら）<select id="chain-from"></select></label>
    <label>条件<select id="chain-on">
      <option value="success">成功したら</option><option value="failure">失敗したら</option><option value="always">成功・失敗にかかわらず終わったら</option></select></label>
    <label>起動先（次に実行するもの）<select id="chain-to"></select></label>
    <label>起動先で実行するジョブ（省略すると全体）<input id="chain-job" placeholder="例: deploy"></label>
    <label>パラメーター（1行に1つ。名前=値）<textarea id="chain-params" rows="3" placeholder="例: ENV=staging"></textarea></label>
    <label class="radio"><input type="checkbox" id="chain-rebuild" checked> 実行済みのコミットでも再実行する</label>
    <label class="radio"><input type="checkbox" id="chain-yes"> 承認ゲートを自動で承認する（管理者の責任で実行されます）</label>
    <label class="radio"><input type="checkbox" id="chain-enabled" checked> 有効</label>
  </div>
  <p class="muted">連鎖がぐるぐる回る組み合わせ（A→B→A など）は登録できません。</p>
  <div class="row"><button data-act="chain-submit" id="chain-submit">追加</button>
    <button class="secondary" data-act="close-modal">キャンセル</button></div>
  <div class="result" id="chain-result"></div>
</div></div>

<div class="overlay" id="edit-overlay"><div class="modal">
  <h3>リポジトリを編集</h3>
  <div class="form-grid">
    <label>名前<input id="edit-name" maxlength="60"></label>
    <label id="edit-path-label">パス<input id="edit-path"></label>
    <label>設定ファイル名<input id="edit-config"></label>
  </div>
  <p class="muted">パスと設定ファイル名は実行中には変更できません。ハブがクローンしたリポジトリのパスは変更できません。</p>
  <div class="row"><button data-act="edit-submit" id="edit-submit">保存</button>
    <button class="secondary" data-act="close-modal">キャンセル</button></div>
  <div class="result" id="edit-result"></div>
</div></div>
<div id="toast"></div>

<script>
  const $ = id => document.getElementById(id);
  let overview = { repos: [], jobs: [], recentRuns: [], isAdmin: false };
  let editingId = null;

  const escapeHtml = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" }[c]));
  const fmtTime = t => t ? new Date(t).toLocaleString() : "–";
  const fmtDuration = s => s >= 60 ? `${Math.floor(s / 60)}分${Math.round(s % 60)}秒` : `${Math.round(s)}秒`;

  function toast(message) {
    const el = $("toast"); el.textContent = message; el.style.display = "block";
    clearTimeout(toast.t); toast.t = setTimeout(() => el.style.display = "none", 4500);
  }

  async function api(url, body) {
    const r = await fetch(url, body === undefined ? undefined
      : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    if (r.status === 401) { location.href = "/login"; throw new Error("unauthorized"); }
    return r;
  }

  function runPill(run, repo) {
    if (repo.running) return '<span class="pill run">実行中</span>';
    if (!run) return '<span class="pill none">未実行</span>';
    return run.isSuccess ? '<span class="pill ok">成功</span>' : '<span class="pill ng">失敗</span>';
  }

  function render() {
    const repos = overview.repos;
    document.body.classList.toggle("role-user", !overview.isAdmin);
    $("t-repos").textContent = repos.length;
    $("t-running").textContent = repos.filter(r => r.running).length;
    $("t-failing").textContent = repos.filter(r => r.lastRun && !r.lastRun.isSuccess).length;
    $("t-jobs").textContent = overview.jobs.length;

    $("repos").innerHTML = repos.length ? repos.map(r => {
      const ready = r.state === "Ready";
      const dot = r.running ? "run" : (r.lastRun ? (r.lastRun.isSuccess ? "ok" : "ng") : "");
      const isJob = r.kind === "workspace";
      const badge = isJob ? ' <span class="pill none">ジョブ</span>' : "";
      const nameCell = ready
        ? `<a class="name" href="/r/${encodeURIComponent(r.id)}/"><span class="dot ${dot}"></span>${escapeHtml(r.name)}</a>${badge}`
        : `<span class="dot"></span><strong>${escapeHtml(r.name)}</strong>${badge}`;
      const sub = isJob
        ? (r.sources && r.sources.length
            ? r.sources.map(s => `<div class="muted">${escapeHtml(s.url)}${s.branch ? " @" + escapeHtml(s.branch) : ""} → ${escapeHtml(s.directory)}</div>`).join("")
            : `<div class="muted">${r.sourceCount ? r.sourceCount + " 件のソース" : "ソースなし"}</div>`)
        : (r.path ? `<div class="muted">${escapeHtml(r.path)}</div>` : "");
      const problem = r.state === "Cloning" ? '<div class="muted">⏳ クローン中…</div>'
        : r.error ? `<div class="err">⚠ ${escapeHtml(r.error)}</div>` : "";
      const last = r.lastRun
        ? `${runPill(r.lastRun, r)} <code>${escapeHtml(r.lastRun.commitHash.slice(0, 8))}</code><div class="muted">${fmtTime(r.lastRun.executedAt)}</div>`
        : runPill(null, r);
      const actions = ready
        ? `<button class="small" data-act="run" data-repo="${escapeHtml(r.id)}" ${r.running ? "disabled" : ""}>▶ 実行</button> `
        : "";
      const admin = r.state === "Cloning" ? "" :
        `<button class="small secondary admin-only" data-act="edit" data-repo="${escapeHtml(r.id)}">編集</button>
         <button class="small danger admin-only" data-act="remove" data-repo="${escapeHtml(r.id)}">削除</button>`;
      return `<tr><td>${nameCell}${sub}${problem}</td><td>${last}</td><td>${r.jobCount}</td>
        <td>${r.scheduleCount ? `${r.scheduleCount} 件${r.nextRunAt ? `<div class="muted">次回 ${fmtTime(r.nextRunAt)}</div>` : ""}` : '<span class="muted">なし</span>'}</td>
        <td class="actions">${actions}${admin}</td></tr>`;
    }).join("") : '<tr><td colspan="5" class="muted">リポジトリがまだ登録されていません。「＋ リポジトリを追加」から登録してください。</td></tr>';

    renderJobs();
    renderChains();

    $("runs").innerHTML = overview.recentRuns.length ? overview.recentRuns.map(run => `<tr>
      <td>${fmtTime(run.executedAt)}</td>
      <td><a class="name" href="/r/${encodeURIComponent(run.repoId)}/">${escapeHtml(run.repoName)}</a></td>
      <td><code>${escapeHtml(run.commit.slice(0, 8))}</code></td>
      <td>${run.success ? '<span class="pill ok">成功</span>' : '<span class="pill ng">失敗</span>'}</td>
      <td>${fmtDuration(run.durationSeconds)}</td>
      <td>${run.failedJobs.length ? escapeHtml(run.failedJobs.join(", ")) : '<span class="muted">–</span>'}</td></tr>`).join("")
      : '<tr><td colspan="6" class="muted">まだ実行履歴がありません。</td></tr>';
  }

  const onLabel = on => on === "success" ? "成功したら" : on === "failure" ? "失敗したら" : "終わったら";
  function renderChains() {
    const chains = overview.chains || [];
    $("chains").innerHTML = chains.length ? chains.map(ch => {
      const from = ch.fromName ? escapeHtml(ch.fromName) : '<span class="err">（削除済み）</span>';
      const to = ch.toName ? escapeHtml(ch.toName) : '<span class="err">（削除済み）</span>';
      const detail = [ch.job ? `ジョブ: ${escapeHtml(ch.job)}` : "", ch.paramCount ? `パラメーター ${ch.paramCount} 件` : "",
        ch.yes ? "自動承認" : "", ch.enabled ? "" : "無効"].filter(Boolean).join(" ・ ");
      const last = ch.lastOutcome
        ? `${escapeHtml(ch.lastOutcome)}<div class="muted">${fmtTime(ch.lastTriggeredAt)}</div>` : '<span class="muted">まだ起動していません</span>';
      return `<tr${ch.enabled ? "" : ' style="opacity:.6"'}><td>${from}</td><td><span class="pill none">${onLabel(ch.on)}</span></td>
        <td>${to}${detail ? `<div class="muted">${detail}</div>` : ""}</td><td>${last}${ch.pending ? `<div class="muted">待機 ${ch.pending} 件</div>` : ""}</td>
        <td class="actions"><button class="small secondary admin-only" data-act="chain-toggle" data-chain="${escapeHtml(ch.id)}">${ch.enabled ? "無効にする" : "有効にする"}</button>
          <button class="small secondary admin-only" data-act="chain-edit" data-chain="${escapeHtml(ch.id)}">編集</button>
          <button class="small danger admin-only" data-act="chain-remove" data-chain="${escapeHtml(ch.id)}">削除</button></td></tr>`;
    }).join("") : '<tr><td colspan="5" class="muted">連鎖はまだありません。「＋ 連鎖を追加」から、「Aが成功したらBを実行」のような設定ができます。</td></tr>';
  }

  function renderJobs() {
    const q = $("job-filter").value.trim().toLowerCase();
    const jobs = overview.jobs.filter(j => !q || (j.repoName + " " + j.name).toLowerCase().includes(q));
    const status = s => !s ? '<span class="muted">–</span>'
      : s === "Success" ? '<span class="pill ok">成功</span>'
      : s === "Failed" ? '<span class="pill ng">失敗</span>'
      : `<span class="pill none">${escapeHtml(s)}</span>`;
    const running = new Set(overview.repos.filter(r => r.running).map(r => r.id));
    $("jobs").innerHTML = jobs.length ? jobs.map(j => `<tr>
      <td><a class="name" href="/r/${encodeURIComponent(j.repoId)}/">${escapeHtml(j.repoName)}</a></td>
      <td>${escapeHtml(j.name)}</td><td>${escapeHtml(j.stage || "–")}</td><td>${status(j.lastStatus)}</td>
      <td class="actions"><button class="small" data-act="run-job" data-repo="${escapeHtml(j.repoId)}" data-job="${escapeHtml(j.name)}"
        ${running.has(j.repoId) ? "disabled" : ""}>▶ 実行</button></td></tr>`).join("")
      : '<tr><td colspan="5" class="muted">該当するジョブがありません。</td></tr>';
  }

  async function load() {
    try {
      const r = await api("/hub/api/overview");
      if (r.ok) { overview = await r.json(); render(); }
    } catch (e) { console.error("ハブの表示更新に失敗しました（次の更新で再試行します）", e); }
  }

  async function startRun(repoId, job) {
    try {
      const r = await api(`/r/${encodeURIComponent(repoId)}/api/run`, { rebuild: false, job: job || null });
      const data = await r.json().catch(() => ({}));
      toast(data.started ? "実行を開始しました。" : (data.reason || "実行を開始できませんでした。"));
    } catch (e) { toast("実行を開始できませんでした。"); }
    load();
  }

  function closeModals() { document.querySelectorAll(".overlay").forEach(o => o.classList.remove("open")); }
  function showResult(id, ok, text) { const el = $(id); el.className = "result " + (ok ? "ok" : "ng"); el.textContent = text; }

  function openAdd() {
    $("add-name").value = ""; $("add-path").value = ""; $("add-url").value = "";
    document.querySelector('input[name=add-kind][value=path]').checked = true; syncAddKind();
    $("add-result").className = "result"; $("add-submit").disabled = false;
    $("add-overlay").classList.add("open"); $("add-name").focus();
  }
  function syncAddKind() {
    const url = document.querySelector("input[name=add-kind]:checked").value === "url";
    $("add-path-label").style.display = url ? "none" : ""; $("add-url-label").style.display = url ? "" : "none";
  }
  async function submitAdd() {
    const isUrl = document.querySelector("input[name=add-kind]:checked").value === "url";
    $("add-submit").disabled = true;
    try {
      const r = await api("/hub/api/repos", { name: $("add-name").value, path: isUrl ? null : $("add-path").value,
        gitUrl: isUrl ? $("add-url").value : null, configFileName: $("add-config").value });
      const data = await r.json();
      if (data.ok) { closeModals(); toast(data.message); load(); return; }
      showResult("add-result", false, data.message || "追加できませんでした。");
    } catch (e) { showResult("add-result", false, "追加できませんでした。"); }
    $("add-submit").disabled = false;
  }

  let jobEditingId = null;
  function sourceRowHtml(s) {
    return `<div class="src-row"><input class="src-url" placeholder="https://github.com/org/repo.git" value="${escapeHtml(s.url || "")}">
      <input class="src-branch" placeholder="main" value="${escapeHtml(s.branch || "")}">
      <input class="src-dir" placeholder="repo" value="${escapeHtml(s.directory || "")}">
      <button class="small danger" data-act="src-remove" type="button">✕</button></div>`;
  }
  function setSources(list) { $("job-sources").innerHTML = list.map(sourceRowHtml).join(""); }
  function readSources() {
    return [...document.querySelectorAll("#job-sources .src-row")].map(row => ({
      url: row.querySelector(".src-url").value, branch: row.querySelector(".src-branch").value,
      directory: row.querySelector(".src-dir").value
    })).filter(s => s.url.trim() || s.branch.trim() || s.directory.trim());
  }
  function openJob(id) {
    jobEditingId = id || null;
    const repo = id ? overview.repos.find(r => r.id === id) : null;
    $("job-title").textContent = repo ? "ジョブを編集" : "ジョブを作成";
    $("job-submit").textContent = repo ? "保存" : "作成";
    $("job-name").value = repo ? repo.name : "";
    setSources(repo ? (repo.sources || []) : [{}]);
    $("job-result").className = "result"; $("job-submit").disabled = false;
    $("job-overlay").classList.add("open"); $("job-name").focus();
  }
  async function submitJob() {
    $("job-submit").disabled = true;
    try {
      const body = jobEditingId
        ? { id: jobEditingId, name: $("job-name").value, sources: readSources() }
        : { kind: "workspace", name: $("job-name").value, sources: readSources() };
      const r = await api(jobEditingId ? "/hub/api/repos/update" : "/hub/api/repos", body);
      const data = await r.json();
      if (data.ok) {
        closeModals(); toast(data.message);
        if (!jobEditingId && data.id) { location.href = `/r/${encodeURIComponent(data.id)}/`; return; }
        load(); return;
      }
      showResult("job-result", false, data.message || "保存できませんでした。");
    } catch (e) { showResult("job-result", false, "保存できませんでした。"); }
    $("job-submit").disabled = false;
  }

  let chainEditingId = null;
  const entryOptions = () => overview.repos.map(r => `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}${r.kind === "workspace" ? "（ジョブ）" : ""}</option>`).join("");
  function openChain(id) {
    if (overview.repos.length < 2) { toast("連鎖には、リポジトリまたはジョブが2つ以上必要です。"); return; }
    chainEditingId = id || null;
    const ch = id ? (overview.chains || []).find(x => x.id === id) : null;
    $("chain-title").textContent = ch ? "連鎖を編集" : "連鎖を追加";
    $("chain-submit").textContent = ch ? "保存" : "追加";
    $("chain-from").innerHTML = entryOptions(); $("chain-to").innerHTML = entryOptions();
    if (ch) { $("chain-from").value = ch.fromId; $("chain-to").value = ch.toId; }
    else { $("chain-to").selectedIndex = 1; }
    $("chain-on").value = ch ? ch.on : "success";
    $("chain-job").value = ch?.job || "";
    $("chain-params").value = ch?.params ? Object.entries(ch.params).map(([k, v]) => `${k}=${v}`).join("\n") : "";
    $("chain-rebuild").checked = ch ? ch.rebuild : true; $("chain-yes").checked = ch ? ch.yes : false; $("chain-enabled").checked = ch ? ch.enabled : true;
    $("chain-result").className = "result"; $("chain-submit").disabled = false;
    $("chain-overlay").classList.add("open");
  }
  function readChainParams() {
    const result = {};
    for (const line of $("chain-params").value.split("\n")) {
      if (!line.trim()) continue;
      const i = line.indexOf("=");
      if (i < 1) throw new Error(`パラメーターは「名前=値」の形式で入力してください: ${line}`);
      result[line.slice(0, i).trim()] = line.slice(i + 1);
    }
    return result;
  }
  async function submitChain() {
    $("chain-submit").disabled = true;
    try {
      const body = { id: chainEditingId, from: $("chain-from").value, to: $("chain-to").value, on: $("chain-on").value,
        job: $("chain-job").value, params: readChainParams(), rebuild: $("chain-rebuild").checked,
        yes: $("chain-yes").checked, enabled: $("chain-enabled").checked };
      const r = await api(chainEditingId ? "/hub/api/chains/update" : "/hub/api/chains", body);
      const data = await r.json();
      if (data.ok) { closeModals(); toast(data.message); load(); return; }
      showResult("chain-result", false, data.message || "保存できませんでした。");
    } catch (e) { showResult("chain-result", false, e.message || "保存できませんでした。"); }
    $("chain-submit").disabled = false;
  }
  async function toggleChain(id) {
    const ch = (overview.chains || []).find(x => x.id === id); if (!ch) return;
    try {
      const r = await api("/hub/api/chains/update", { id, enabled: !ch.enabled });
      toast((await r.json()).message);
    } catch (e) { toast("変更できませんでした。"); }
    load();
  }
  async function removeChain(id) {
    if (!confirm("この連鎖を削除しますか？")) return;
    try {
      const r = await api("/hub/api/chains/remove", { id });
      toast((await r.json()).message);
    } catch (e) { toast("削除できませんでした。"); }
    load();
  }

  function openEdit(id) {
    const repo = overview.repos.find(r => r.id === id); if (!repo) return;
    if (repo.kind === "workspace") { openJob(id); return; }
    editingId = id;
    $("edit-name").value = repo.name; $("edit-path").value = repo.path || ""; $("edit-config").value = repo.configFileName;
    $("edit-path").disabled = !!repo.cloneUrl;
    $("edit-result").className = "result"; $("edit-submit").disabled = false;
    $("edit-overlay").classList.add("open");
  }
  async function submitEdit() {
    $("edit-submit").disabled = true;
    try {
      const r = await api("/hub/api/repos/update", { id: editingId, name: $("edit-name").value,
        path: $("edit-path").disabled ? null : $("edit-path").value, configFileName: $("edit-config").value });
      const data = await r.json();
      if (data.ok) { closeModals(); toast(data.message); load(); return; }
      showResult("edit-result", false, data.message || "保存できませんでした。");
    } catch (e) { showResult("edit-result", false, "保存できませんでした。"); }
    $("edit-submit").disabled = false;
  }

  async function removeRepo(id) {
    const repo = overview.repos.find(r => r.id === id); if (!repo) return;
    if (!confirm(`「${repo.name}」をハブの登録から外しますか？\n（${repo.kind === "workspace" ? "ジョブのファイルや実行履歴" : "リポジトリのファイルや実行履歴"}は削除されません）`)) return;
    try {
      const r = await api("/hub/api/repos/remove", { id });
      const data = await r.json(); toast(data.message);
    } catch (e) { toast("削除できませんでした。"); }
    load();
  }

  document.addEventListener("click", ev => {
    const el = ev.target.closest("[data-act]"); if (!el) return;
    const repo = el.dataset.repo;
    switch (el.dataset.act) {
      case "run": startRun(repo); break;
      case "run-job": startRun(repo, el.dataset.job); break;
      case "edit": openEdit(repo); break;
      case "remove": removeRepo(repo); break;
      case "open-add": openAdd(); break;
      case "open-chain": openChain(); break;
      case "chain-submit": submitChain(); break;
      case "chain-edit": openChain(el.dataset.chain); break;
      case "chain-toggle": toggleChain(el.dataset.chain); break;
      case "chain-remove": removeChain(el.dataset.chain); break;
      case "open-job": openJob(); break;
      case "job-submit": submitJob(); break;
      case "src-add": $("job-sources").insertAdjacentHTML("beforeend", sourceRowHtml({})); break;
      case "src-remove": el.closest(".src-row").remove(); break;
      case "add-submit": submitAdd(); break;
      case "edit-submit": submitEdit(); break;
      case "close-modal": closeModals(); break;
    }
  });
  document.querySelectorAll("input[name=add-kind]").forEach(el => el.addEventListener("change", syncAddKind));
  $("job-filter").addEventListener("input", renderJobs);
  document.addEventListener("keydown", ev => { if (ev.key === "Escape") closeModals(); });

  (async function init() {
    try {
      const me = await (await fetch("/api/me")).json();
      if (me.authEnabled) $("whoami").textContent = `${me.name}（${me.role === "admin" ? "管理者" : "ユーザー"}）`;
      if (me.authEnabled && me.kind !== "token") $("logout-form").style.display = "";
    } catch (e) {}
    load(); setInterval(load, 5000);
  })();
</script>
</body>
</html>
""";
}
