using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SebastianCi.Models;

namespace SebastianCi.Web;

/// <summary>
/// ハブ固有のエンドポイント（全リポジトリの概況・登録・編集・削除）と、リポジトリ詳細ページの配信。
/// 各リポジトリの操作は `/r/{repoId}/api/...` に、単体 `serve` と同じ API が並ぶ。
/// </summary>
public sealed class HubApi(HubService hub, AccessControl accessControl)
{
    private const int RecentRunsPerRepository = 10;
    private const int RecentRunsOverall = 30;

    private static readonly Regex RepositoryRoute = new("^/r/([a-z0-9][a-z0-9-]{0,39})(?:/|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(HubPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/hub/api/overview", (HttpContext c, CancellationToken ct) => GetOverviewAsync(c, ct));
        app.MapPost("/hub/api/repos", (AddRepositoryPayload p) => AddRepository(p));
        app.MapPost("/hub/api/repos/update", (UpdateRepositoryPayload p) => ToResult(hub.Update(p.Id, p.Name, p.Path, p.ConfigFileName)));
        app.MapPost("/hub/api/repos/remove", (RemoveRepositoryPayload p) => ToResult(hub.Remove(p.Id)));

        // ASP.NET は末尾スラッシュの有無を同一ルートとして扱うため、1つのハンドラで「/r/id → /r/id/」の転送とページ配信を兼ねる。
        app.MapGet("/r/{repoId}", (HttpContext context, string repoId) =>
        {
            if (hub.Find(repoId) is not { } view) return Results.NotFound();

            return context.Request.Path.Value!.EndsWith('/')
                ? Results.Content(RepositoryPage.Render(view.Entry.Id, view.Entry.Name), "text/html; charset=utf-8")
                : Results.Redirect($"/r/{view.Entry.Id}/");
        });
        RepositoryApi.MapEndpoints(app, "/r/{" + RepositoryApi.RouteKey + "}");
    }

    /// <summary>`/r/{id}/…` の要求で、登録が無い（404）・クローン中や失敗（409）を API より手前で返す。</summary>
    public async Task RequireRepositoryAsync(HttpContext context, RequestDelegate next)
    {
        Match match = RepositoryRoute.Match(context.Request.Path.Value ?? "");
        if (!match.Success)
        {
            await next(context);
            return;
        }

        RepositoryView? view = hub.Find(match.Groups[1].Value);
        if (view is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("リポジトリが見つかりません。");
            return;
        }

        if (view.Api is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync(view.State == RepositoryState.Cloning ? "クローン中です。" : view.Error ?? "利用できません。");
            return;
        }

        await next(context);
    }

    private IResult AddRepository(AddRepositoryPayload payload)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(payload.Path);
        bool hasUrl = !string.IsNullOrWhiteSpace(payload.GitUrl);
        if (hasPath == hasUrl)
        {
            return ToResult(new HubResult(false, "ローカルのパスか Git の URL のどちらか一方を指定してください。"));
        }

        return ToResult(hasPath
            ? hub.AddLocal(payload.Name, payload.Path, payload.ConfigFileName)
            : hub.AddByClone(payload.Name, payload.GitUrl, payload.ConfigFileName));
    }

    private static IResult ToResult(HubResult result)
        => Results.Json(new { ok = result.Ok, message = result.Message, id = result.Id });

    private async Task<IResult> GetOverviewAsync(HttpContext context, CancellationToken cancellationToken)
    {
        bool isAdmin = accessControl.IsAdmin(context);
        List<object> repos = [];
        List<object> jobs = [];
        List<(string Id, string Name, RunSummary Run)> runs = [];

        foreach (RepositoryView view in hub.Snapshot())
        {
            RepositorySummary? summary = await TrySummarizeAsync(view, cancellationToken);
            repos.Add(new
            {
                id = view.Entry.Id,
                name = view.Entry.Name,
                path = isAdmin ? view.Entry.Path : null,
                cloneUrl = isAdmin ? view.Entry.CloneUrl : null,
                configFileName = view.Entry.ConfigFileName,
                state = view.State,
                error = view.Error ?? summary?.ConfigError,
                running = summary?.Running ?? false,
                configExists = summary?.ConfigExists ?? false,
                jobCount = summary?.Jobs.Count ?? 0,
                scheduleCount = summary?.ScheduleCount ?? 0,
                nextRunAt = summary?.NextRunAt,
                lastRun = summary?.LastRun
            });

            if (summary is null) continue;

            jobs.AddRange(summary.Jobs.Select(job => (object)new
            {
                repoId = view.Entry.Id, repoName = view.Entry.Name, name = job.Name, stage = job.Stage, lastStatus = job.LastStatus
            }));
            runs.AddRange(summary.RecentRuns.Select(run => (view.Entry.Id, view.Entry.Name, run)));
        }

        var recentRuns = runs
            .OrderByDescending(item => item.Run.ExecutedAt)
            .Take(RecentRunsOverall)
            .Select(item => new
            {
                repoId = item.Id, repoName = item.Name, commit = item.Run.CommitHash, success = item.Run.IsSuccess,
                executedAt = item.Run.ExecutedAt, durationSeconds = item.Run.DurationSeconds, failedJobs = item.Run.FailedJobs
            });
        return Results.Json(new { isAdmin, repos, jobs, recentRuns, serverTime = DateTimeOffset.Now });
    }

    private static async Task<RepositorySummary?> TrySummarizeAsync(RepositoryView view, CancellationToken cancellationToken)
    {
        if (view.Api is null) return null;

        try
        {
            return await view.Api.GetSummaryAsync(RecentRunsPerRepository, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 1つのリポジトリの履歴が壊れていても、他のリポジトリの表示は止めない。
            return new RepositorySummary(true, $"状態を読み取れません: {exception.Message}", view.Api.IsRunning, [], 0, null, null, []);
        }
    }

    private sealed record AddRepositoryPayload(string? Name, string? Path, string? GitUrl, string? ConfigFileName);
    private sealed record UpdateRepositoryPayload(string? Id, string? Name, string? Path, string? ConfigFileName);
    private sealed record RemoveRepositoryPayload(string? Id);
}
