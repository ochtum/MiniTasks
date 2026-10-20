# ローカル開発

MiniTasks 専用の Supabase Local と Blazor アプリを使います。クラウドへの接続・作成・デプロイは行いません。

## 前提

- .NET 10 SDK（リポジトリの `global.json` に従う）
- PowerShell 7 以降（Windows/Linux）
- 稼働中の Docker Linux エンジン
- Supabase CLI **2.114.0**。スクリプトは異なるバージョンを拒否します。

CLI 導入例は [公式 CLI ガイド](https://supabase.com/docs/guides/local-development/cli/getting-started) を参照してください。既存の CLI を自動更新する処理はありません。

## 初回セットアップと起動

リポジトリのルートで実行します。

```powershell
pwsh ./scripts/setup-local.ps1
pwsh ./scripts/start-local.ps1
```

`setup-local.ps1` は `supabase start` → `supabase migration up --local` → Auth Admin API によるデモユーザー作成・再利用 → .NET user-secrets 設定を実行します。既存ユーザーのパスワードを上書きせず、指定したデモパスワードでログインできることを確認します。初回は Docker イメージの取得が必要です。

`start-local.ps1` は同じセットアップを確認してから `ASPNETCORE_ENVIRONMENT=Development` で `dotnet run --no-launch-profile --urls http://localhost:5050` を実行します。これにより開発用 user-secrets を読み込みます。Supabase だけを起動する場合は `-SupabaseOnly` を指定します。通常のセットアップ・起動では DB をリセットしません。アプリは `Ctrl+C` で停止します。

| 用途 | URL / 設定 |
| --- | --- |
| アプリ | `http://localhost:5050` |
| Supabase API | `http://127.0.0.1:54421` |
| PostgreSQL | `127.0.0.1:54422` |
| Studio | `http://127.0.0.1:54423` |
| ローカルメール確認 | `http://127.0.0.1:54424` |
| ローカル project_id | `minitasks` |

デモログインは `alice@minitasks.local` と `bob@minitasks.local`、共通パスワードは `MiniTasks-local-2026!` です。これは専用ローカル環境向けの公開デモ認証情報です。サインアップは無効にし、セットアップが確認済みユーザーを作成します。

アプリに保存するのは `UserSecretsId=MiniTasks.Web.Local` の `Supabase:Url` と公開可能な `Supabase:ApiKey`（publishable key、未提供時は anon key）だけです。管理キーは Auth ユーザー準備の間だけメモリー上で利用し、アプリ設定やファイルに保存しません。user-secrets は開発用設定の保存場所で、暗号化された保管庫ではありません。

## データと権限

`public.tasks` は `id`（bigint identity）、`owner_id`（Auth ユーザー UUID）、`title`（空白だけは不可、1〜200 文字）、`is_completed`、`created_at` を持ちます。`owner_id` は JWT の `auth.uid()` を既定値とし、ユーザー削除時に関連タスクも削除します。

`authenticated` にのみ CRUD 権限を付与し、各操作の RLS で `auth.uid() = owner_id` を検証します。UPDATE は更新前と更新後の両方を制限し、別ユーザーへの所有権変更も拒否します。公開キーだけではタスクを読めません。アプリの一覧フィルターに依存せず、DB で利用者を隔離します。

## 検証

```powershell
pwsh ./scripts/test-supabase.ps1
```

最初に `supabase test db --local` で pgTAP を実行し、その後 2 ユーザーの実際のパスワードログインで取得した JWT を用いて REST API を試験します。CRUD、フィルターなしの一覧隔離、既知 ID に対する他人の読み取り・変更・削除、所有者偽装・変更、入力制約、未認証・不正 JWT、公開サインアップの禁止を確認します。SQL 試験はトランザクションをロールバックし、REST 試験は当該実行で作ったタスクだけを本人の JWT で削除します。既存の手入力タスクを削除しません。

REST 試験だけを再実行する場合は `-SkipPgTap` を使えます。トークン・API キー・管理キーはログに出力しません。CLI が返す管理キーを含む起動・状態出力もスクリプト内で捕捉します。

CI は `pwsh ./scripts/setup-local.ps1 -SkipUserSecrets` → `pwsh ./scripts/test-supabase.ps1` を呼び、最後に `pwsh ./scripts/stop-local.ps1` を実行します。DB リセットは不要です。

## 停止と操作範囲

```powershell
pwsh ./scripts/stop-local.ps1
```

`supabase stop --project-id minitasks` で専用環境だけを停止し、DB データは保持します。他の Supabase Local プロジェクトを停止しません。54420〜54429 はこのサンプル向けに確保してください（未使用のポートも含みます）。

共通ヘルパーは project_id と API/DB/Studio/メールのポート、CLI バージョン、クラウドとの未リンク状態を確認し、REST の接続先を `http://127.0.0.1:54421` に限定します。`--linked`、`--db-url`、`--project-ref`、`--all`、`--no-backup` を受け付けません。Docker の他プロジェクト設定は変更しません。

スキーマ変更は `supabase migration new <名前>` で新しいファイルを作り、`supabase migration up --local` で適用します。共有済み migration の書き換えや、通常起動への `db reset` の追加は避けてください。ローカルの Auth データも含めて初期化する作業は通常のフローに含めていません。

## 参照

- [Supabase ローカル開発](https://supabase.com/docs/guides/local-development/cli/getting-started)
- [データベーステスト](https://supabase.com/docs/guides/database/testing)
- [Auth を含むテストの考え方](https://supabase.com/docs/guides/local-development/testing/overview)
- [Row Level Security](https://supabase.com/docs/guides/database/postgres/row-level-security)
