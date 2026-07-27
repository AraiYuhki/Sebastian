namespace SebastianCi.Web;

/// <summary>テンプレートの入力項目1つ分。Options があれば選択式、Multiple なら複数選択。</summary>
public sealed record TemplateField(
    string Key, string Label, string DefaultValue, string? Hint = null,
    List<string>? Options = null, bool Multiple = false);

/// <summary>ダッシュボードに表示するビルドテンプレート1件分。</summary>
public sealed record JobTemplate(string Id, string Name, string Description, List<TemplateField> Fields);

/// <summary>
/// ゲームエンジン（Godot / Unity / Unreal Engine）のビルドジョブを、少ない入力で生成するテンプレート集。
/// 内容は examples/ のサンプル定義と同じ構成で、入力値を埋め込んだジョブのマップを組み立てる。
/// </summary>
public static class JobTemplateCatalog
{
    public static IReadOnlyList<JobTemplate> Templates { get; } =
    [
        new JobTemplate(
            "godot", "Godot 4",
            "godot --headless でプロジェクトを書き出します。書き出しプリセット（export_presets.cfg）がプロジェクトに必要です。",
            [
                new TemplateField("jobName", "ジョブ名", "godot-export"),
                new TemplateField("version", "Godot バージョン", "4.2.2",
                    "barichello/godot-ci イメージのタグ。プロジェクトのバージョンに合わせてください"),
                new TemplateField("preset", "書き出しプリセット名", "Linux/X11",
                    "export_presets.cfg に定義したプリセットの名前"),
                new TemplateField("output", "出力先パス", "build/linux/game.x86_64",
                    "書き出すバイナリのパス（リポジトリからの相対パス）"),
            ]),
        new JobTemplate(
            "unity", "Unity",
            "unityci/editor イメージでバッチモードビルドします。ホスト環境変数 UNITY_LICENSE にライセンス（.ulf の中身）を設定しておいてください。",
            [
                new TemplateField("jobName", "ジョブ名", "unity-build"),
                new TemplateField("image", "エディタイメージ", "unityci/editor:ubuntu-2022.3.10f1-linux-il2cpp-3",
                    "Unity バージョンとモジュールに合わせてタグを変更してください"),
                new TemplateField("targets", "ビルドターゲット", "StandaloneLinux64,StandaloneWindows64",
                    "複数選ぶと matrix で並列ビルドされます",
                    ["StandaloneLinux64", "StandaloneWindows64", "StandaloneOSX", "WebGL", "Android", "iOS"],
                    Multiple: true),
                new TemplateField("method", "ビルドメソッド", "BuildScript.PerformBuild",
                    "-executeMethod で呼び出す静的メソッド（プロジェクト側に必要）"),
            ]),
        new JobTemplate(
            "unreal", "Unreal Engine",
            "RunUAT の BuildCookRun でパッケージ化します。ghcr.io/epicgames/unreal-engine イメージの利用には Epic Games / GitHub のアカウント連携が必要です（イメージは数十 GB あります）。",
            [
                new TemplateField("jobName", "ジョブ名", "unreal-package"),
                new TemplateField("version", "エンジンバージョン", "5.3",
                    "ghcr.io/epicgames/unreal-engine:dev-<バージョン> のタグに使われます"),
                new TemplateField("uproject", ".uproject ファイル名", "MyGame.uproject",
                    "リポジトリ直下にあるプロジェクトファイル"),
                new TemplateField("platform", "ターゲットプラットフォーム", "Linux", null,
                    ["Linux", "Win64", "Mac", "Android"]),
                new TemplateField("config", "ビルド構成", "Development", null,
                    ["Development", "Shipping", "DebugGame"]),
            ]),
    ];

    /// <summary>入力値を埋め込んだジョブ（名前と、YAMLへ書き込むキー→値のマップ）を組み立てる。</summary>
    public static (string JobName, Dictionary<string, object> Job) Instantiate(
        string id, IReadOnlyDictionary<string, object> values) => id switch
    {
        "godot" => BuildGodot(values),
        "unity" => BuildUnity(values),
        "unreal" => BuildUnreal(values),
        _ => throw new ArgumentException($"未知のテンプレートです: {id}"),
    };

    private static (string, Dictionary<string, object>) BuildGodot(IReadOnlyDictionary<string, object> values)
    {
        string version = GetText(values, "version", "4.2.2");
        string preset = GetText(values, "preset", "Linux/X11");
        string output = GetText(values, "output", "build/linux/game.x86_64");
        int slash = output.LastIndexOf('/');
        string outputDirectory = slash > 0 ? output[..slash] : ".";

        Dictionary<string, object> job = new()
        {
            ["image"] = $"barichello/godot-ci:{version}",
            ["timeout"] = 1800,
            ["cache"] = new List<string> { "/root/.local/share/godot/export_templates" },
            ["script"] = new List<string>
            {
                $"mkdir -p {outputDirectory}",
                "godot --headless --import",
                $"godot --headless --export-release \"{preset}\" {output}",
            },
            ["artifacts"] = new List<string> { outputDirectory == "." ? output : outputDirectory },
        };
        return (GetText(values, "jobName", "godot-export"), job);
    }

    private static (string, Dictionary<string, object>) BuildUnity(IReadOnlyDictionary<string, object> values)
    {
        string image = GetText(values, "image", "unityci/editor:ubuntu-2022.3.10f1-linux-il2cpp-3");
        string method = GetText(values, "method", "BuildScript.PerformBuild");
        List<string> targets = GetList(values, "targets", ["StandaloneLinux64"]);
        bool useMatrix = targets.Count > 1;
        string target = useMatrix ? "$target" : targets[0];

        Dictionary<string, object> job = new() { ["image"] = image };
        if (useMatrix) job["matrix"] = new Dictionary<string, object> { ["target"] = targets };
        job["timeout"] = 3600;
        job["retry"] = 1;
        job["cache"] = new List<string> { "/root/.cache/unity3d", "/root/.config/unity3d" };
        job["env"] = new Dictionary<string, string> { ["UNITY_LICENSE"] = "$UNITY_LICENSE" };
        job["script"] = new List<string>
        {
            "unity-editor -quit -batchmode -nographics -logFile /dev/stdout -projectPath . "
                + $"-buildTarget \"{target}\" -executeMethod {method}",
        };
        job["artifacts"] = new List<string> { "build" };
        return (GetText(values, "jobName", "unity-build"), job);
    }

    private static (string, Dictionary<string, object>) BuildUnreal(IReadOnlyDictionary<string, object> values)
    {
        string version = GetText(values, "version", "5.3");
        string uproject = GetText(values, "uproject", "MyGame.uproject");
        string platform = GetText(values, "platform", "Linux");
        string configuration = GetText(values, "config", "Development");

        Dictionary<string, object> job = new()
        {
            ["image"] = $"ghcr.io/epicgames/unreal-engine:dev-{version}",
            ["timeout"] = 7200,
            ["cache"] = new List<string> { "/root/ue-ddc" },
            ["env"] = new Dictionary<string, string> { ["UE-LocalDataCachePath"] = "/root/ue-ddc" },
            ["script"] = new List<string>
            {
                "/home/ue4/UnrealEngine/Engine/Build/BatchFiles/RunUAT.sh BuildCookRun "
                    + $"-project=\"/workspace/{uproject}\" -platform={platform} -clientconfig={configuration} "
                    + "-cook -build -stage -pak -archive -archivedirectory=\"/workspace/build\"",
            },
            ["artifacts"] = new List<string> { "build" },
        };
        return (GetText(values, "jobName", "unreal-package"), job);
    }

    private static string GetText(IReadOnlyDictionary<string, object> values, string key, string fallback)
        => values.TryGetValue(key, out object? value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : fallback;

    private static List<string> GetList(IReadOnlyDictionary<string, object> values, string key, List<string> fallback)
        => values.TryGetValue(key, out object? value) && value is List<string> { Count: > 0 } list
            ? list
            : fallback;
}
