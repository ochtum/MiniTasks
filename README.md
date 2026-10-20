# MiniTasks

**Blazor Web App＋Azure App Service＋Supabase**で、ローカル開発から公開までを試す小さなタスク管理サンプルです。画面と業務処理をC#で実装し、Supabase Authと、利用者のJWTを使うData APIへ接続します。

## できること

- メールアドレスとパスワードでログイン・ログアウト
- 自分のタスクを追加・一覧表示・完了切り替え・削除
- すべて／未完了／完了で表示を切り替え
- 別利用者のタスクへの参照・更新・削除をDBのRLSで拒否
- 再読込時のログイン維持、アクセストークンの更新
- ローカルの起動・テストと、GitHub ActionsからAzureへの公開

初版は.NET 10のInteractive Server、App Serviceの**1インスタンス**を対象とします。Supabaseのトークンはサーバーのメモリー内でログインごとに分離して保持し、ブラウザーへ渡す認証CookieにはアプリのセッションIDなどを保存します。サーバー再起動・再配置時、またはログインから8時間経過後は再ログインします。

## 最初に動かす

前提は.NET SDK **10.0.401**、PowerShell 7、DockerのLinuxエンジン、Supabase CLI **2.114.0**です。SDKは`global.json`で完全一致に固定しています。SDKを更新する際は、SDKが追加する依存も含めてLockファイルを更新し、CIで確認します。

```powershell
cd D:\source\MiniTasks
pwsh ./scripts/setup-local.ps1
pwsh ./scripts/start-local.ps1
```

ブラウザーで[http://localhost:5050](http://localhost:5050)を開きます。初回はDockerイメージの取得が入ります。通常の起動では既存のタスクを保持します。

| ローカルの利用者 | パスワード |
| --- | --- |
| `alice@minitasks.local` | `MiniTasks-local-2026!` |
| `bob@minitasks.local` | `MiniTasks-local-2026!` |

これはローカル専用のデモ認証情報です。公開環境では自分のAuth利用者を作成します。ローカルの設定・データ・デモ利用者はデプロイしません。

セットアップは、専用Supabaseの起動、未適用Migrationの反映、Auth管理APIによる利用者作成、.NET User Secretsへの接続設定を行います。管理キーは初期準備だけで使用し、アプリへは保存しません。

アプリを`Ctrl+C`で終了した後、専用Supabaseを停止できます。データは保持します。

```powershell
pwsh ./scripts/stop-local.ps1
```

詳しい前提、手順、ポート、データの扱いは[ローカル開発手順](docs/local-development.md)を参照してください。

## 手動でアプリを起動する場合

`setup-local.ps1`の完了後、次のコマンドでも起動できます。開発用の接続設定は`UserSecretsId=MiniTasks.Web.Local`で読み込みます。

```powershell
dotnet run --project src/MiniTasks.Web
```

| 接続先 | URL |
| --- | --- |
| アプリ | [http://localhost:5050](http://localhost:5050) |
| Supabase Studio | [http://127.0.0.1:54423](http://127.0.0.1:54423) |
| アプリの応答確認 | [http://localhost:5050/healthz](http://localhost:5050/healthz) |

Supabaseには専用の`project_id=minitasks`と54420〜54429のポート帯を使います。他のSupabaseプロジェクトを停止・初期化するスクリプトは含みません。

## 動作を確認する

1. Aliceでログインし、タスクを追加します。
2. 完了に切り替え、絞り込みと再読込を試します。
3. 別のブラウザーまたはプライベートウィンドウでBobとしてログインします。
4. Aliceのタスクが表示されず、Bobのタスクを追加できることを確認します。
5. 削除ボタンの確認操作と、ログアウト後の再アクセスを試します。

## 自動テスト

外部サービスへ接続しない.NETのテストと、実際のSupabase Localを使うDB・RESTテストを用意しています。

```powershell
dotnet restore MiniTasks.sln --locked-mode
dotnet test MiniTasks.sln -c Release --no-restore

# Supabase Localが未起動なら準備する
pwsh ./scripts/setup-local.ps1
pwsh ./scripts/test-supabase.ps1

# アプリが起動している状態で確認する
pwsh ./scripts/test-health.ps1 -BaseUrl http://localhost:5050
```

.NET側では認証の分離、トークン更新、ログアウト、CSRF、Cookie、リダイレクト、タスクAPIへのリクエストを確認します。DB側ではpgTAPと2利用者の実際のJWTでCRUD・RLS・入力制約を確認します。RESTテストは、その実行で作成したタスクだけを後片付けします。

作成時の実行結果と確認範囲は[検証結果](docs/verification.md)に記載しています。

## Azureへ公開する

初回にLinux App Service、Hosted Supabase、GitHubのOIDC連携と`staging` Environmentを用意します。[Azureへのデプロイ手順](docs/azure-deployment.md)に、必要な権限・Variables・Secretsをまとめています。

HostedのMigrationには、GitHub Environmentの`SUPABASE_PROJECT_REF`、`SUPABASE_DB_POOLER_HOST`と、Secretの`SUPABASE_DB_PASSWORD`を使います。CLIはIPv4対応のSession poolerへ接続し、パスワードはURLに含めず`PGPASSWORD`で渡します。管理PATや`supabase link`は不要です。アプリ実行時のデータ通信は、利用者JWTを使うHTTPSのData APIです。

App Serviceが読む設定は次の4つです。

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
Supabase__Url=https://<project-ref>.supabase.co
Supabase__ApiKey=<同じProjectのpublishableキー>
```

GitHubへソースを配置し、`main`を既定Branchとして、Actionsの`Deploy staging`を手動実行します。

| Workflow | 実行内容 |
| --- | --- |
| `ci.yml` | Push／PRで共通の検査Workflowを実行 |
| `verify.yml` | Lock付きrestore、Release build、.NETテスト、Supabase LocalのDB・REST試験、publish |
| `deploy-staging.yml` | 同じ実行内の検査に成功した成果物で、HostedのMigration、App Service配置、ヘルス確認 |

公開先でのDB変更は旧アプリと互換性のある追加変更を前提とします。DBとアプリを一括で自動Rollbackする仕組みは含みません。公開後は、ヘルス応答に加え、ログイン・タスク操作・利用者分離も確認してください。

## ソースの読み方

```text
src/MiniTasks.Web/
  Authentication/        サーバーセッション、Cookie認証、更新・失効
  Pages/Account/          通常のHTTP POSTで処理するログイン・ログアウト
  Components/Pages/      Blazorのタスク画面
  Tasks/                 利用者のJWTで呼ぶData API
  Infrastructure/        接続設定
supabase/
  migrations/            テーブル・GRANT・RLS
  tests/                 pgTAPによるDB検証
scripts/                 ローカル準備・起動・停止・実JWT試験・ヘルス確認
tests/MiniTasks.Tests/   .NET単体・HTTP認証フローテスト
.github/workflows/      CIとAzureへの公開
docs/                    ローカルとAzureの詳しい手順
```

認証・データ通信は標準の`HttpClient`でSupabase REST APIを呼びます。リクエストごとに`apikey`と利用者のBearerトークンを設定し、共有のHTTPヘッダーへ利用者の状態を書き込みません。画面の所有者フィルターに頼らず、DBのRLSでも権限を検査します。

一覧は新しい順に最大1,000件を取得し、集計と絞り込みも取得分を対象とします。Hosted側のData API上限が低い場合は、その件数が上限です。大量データを扱う場合はページングを追加します。

このサンプルには新規登録・メール確認・パスワード再設定・チーム共有・添付ファイルを含めていません。継続利用や複数インスタンスへ拡張する場合は、共有セッション保存先、Data Protectionの鍵管理などを追加設計します。

## 参考資料

- [Blazorの認証と認可](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0)
- [Supabase Authのセッション](https://supabase.com/docs/guides/auth/sessions)
- [SupabaseのRLS](https://supabase.com/docs/guides/database/postgres/row-level-security)
- [App ServiceとGitHub Actions](https://learn.microsoft.com/en-us/azure/app-service/deploy-github-actions)

クラウドでのWorkflow実行とAzureへの配置は、公開先の設定を行ってから確認します。
