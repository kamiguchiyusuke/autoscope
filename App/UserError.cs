using System.IO;
namespace ArmadaAnalyzer.Desktop;
internal static class UserError
{
    internal static string Message(Exception error)=>error switch
    {
        UnauthorizedAccessException=>"ファイルへのアクセス権限がありません。保存先・読込元を確認してください。",
        FileNotFoundException or DirectoryNotFoundException=>"必要なファイルまたはフォルダーが見つかりません。読込元・保存先を確認してください。",
        IOException=>"ファイルまたは分析データにアクセスできません。空き容量と、他のアプリで使用中でないかを確認してください。",
        OutOfMemoryException=>"メモリが不足しています。対象データや分析範囲を減らしてください。",
        OperationCanceledException=>"処理を中止しました。",
        _ when error.Message.Any(c=>c is >= '\u3040' and <= '\u30ff')=>error.Message,
        _=>"処理を完了できませんでした。診断ログを確認してください。"
    };
}
