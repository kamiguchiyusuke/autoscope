# Third-party components and source availability

自作のApp/Core/Contracts/Native/SamplePlugins/Testsと文書はルートのMITライセンスです。


| Component | Source / license | Distribution |
|---|---|---|
| .NET / WPF | [dotnet/runtime](https://github.com/dotnet/runtime), [dotnet/wpf](https://github.com/dotnet/wpf): MITおよび各サードパーティ通知 | 公式.NETバイナリにはMicrosoft .NET Library条件も適用。配布物のLegalを参照 |
| SQLite 3.54.0 | [sqlite.org](https://www.sqlite.org/copyright.html): public domain | `Vendor/sqlite/sqlite3.c` と `.h` を同梱し、DLLをソースからビルド |
| MSVC/Windows SDK | 無料Build Tools/SDK。各Microsoftライセンス | ビルドツールは配布に含めない。MSVCビルドでは静的CRTを使用 |

.NETの配布条件と通知は `Legal/DOTNET-LICENSE.txt`, `Legal/DOTNET-THIRD-PARTY-NOTICES.txt` に含めます。
自作コードのMITはこれら第三者コンポーネントをMITに再ライセンスするものではありません。
SQLiteの同梱はpublic-domainのamalgamationのみで、別ライセンスのconfigure等のビルドスクリプトは含めません。

確認した一次情報:

- https://www.sqlite.org/copyright.html
- https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- https://github.com/dotnet/core/blob/main/license-information.md

公開ソースを再ビルド・改変するために、非公開の必須部品、認証サーバー、有料SDKは不要です。

Installer builder: Inno Setup 6.7.3, Jordan Russell / Martijn Laan. Official source https://github.com/jrsoftware/issrc ; license: Legal/INNO-SETUP-LICENSE.txt. Unmodified installer runtime; the analyzer itself remains MIT.

CPython 3.12 runtime: Python Software Foundation License; included license: `App/TdsBridge/python/LICENSE.txt`. Source: https://www.python.org/downloads/source/ . Runtime DLLs retain their upstream distribution terms.
