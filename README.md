# Auto Scope 1.01

Windows向けストラテジーテスターレポート分析アプリ。

## ビルド

Windows x64、.NET SDK 10、Visual Studio C++ Build Tools（C++ワークロード・Windows SDK）、PowerShellを用意します。

```powershell
./scripts/build.ps1
```

成果物は `artifacts/package/AutoScope.exe` です。初回は.NETランタイムパックの取得にネットワーク接続が必要です。TDS連携にはCPython 3.12 x64ランタイムを使用します。リポジトリには含めないため、python.orgから取得した `python.exe`・`python312.dll`・`python312.zip`・`DLLs/` などを `App/TdsBridge/python/` に配置してください（`python312._pth` は同梱）。MT4・MT5・Tick Data Suite本体、認証情報、ヒストリカルデータは含みません。連携を使用する場合は各ソフトを別途用意してください。

## テスト

```powershell
dotnet run --project Tests -c Release -- all
dotnet run --project AutoBacktest.Tests -c Release
./artifacts/package/AutoScope.exe --smoke ./artifacts/test-data ./artifacts/test-output
./App/TdsBridge/python/python.exe -m unittest discover -s App/TdsBridge/tests
```

テスト用データには同梱の合成サンプルを使用します。実際のMT連携テストには別途端末とEAが必要です。

## インストーラー

Inno Setup 6を用意し、ビルド後に実行します。

```powershell
./scripts/build-installer.ps1 -AppSource artifacts/package -SetupOutput artifacts/installer -Compiler "C:/Program Files (x86)/Inno Setup 6/ISCC.exe"
```

## 構成

- `App`: WPF画面、レポート出力、TDS連携
- `Core`: 読込、計算、保存
- `AutoBacktest`: MT4・MT5連続バックテスト
- `Contracts` / `SamplePlugins`: 拡張用インターフェースとサンプル
- `Native` / `Vendor`: ネイティブ処理とSQLite
- `Tests` / `AutoBacktest.Tests`: 自動テスト

## ライセンス

自作コードはMITライセンスです。`LICENSE`、`THIRD_PARTY_NOTICES.md`および各同梱コンポーネントのライセンスを参照してください。
