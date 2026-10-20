# 検証結果

2026年10月19日、Windows・.NET SDK 10.0.401・Supabase CLI 2.114.0・DockerのLinuxエンジンで確認しました。

| 対象 | 結果 |
| --- | --- |
| `dotnet restore MiniTasks.sln --locked-mode` | 成功 |
| Releaseの.NETテスト | 34件成功、失敗・スキップ0 |
| `dotnet publish`のRelease出力 | 成功 |
| Supabase LocalのpgTAP | 22件成功 |
| 実際のAuth利用者JWTを使うREST試験 | 20件成功 |
| LocalのDB advisors | 問題なし |
| setupの再実行 | 既存の利用者とタスクを保持 |
| `start-local.ps1`でのアプリ起動 | 成功 |
| `/healthz` | HTTP 200、`status=ok`、`app=MiniTasks` |
| 3つのWorkflowの構文 | actionlint 1.7.12で成功 |
| ヘルス確認用スクリプト | PowerShell 5.1と7で計14ケース成功 |

実ブラウザーでは、Aliceのログイン、タスク追加、完了切り替え、絞り込み、再読込後の状態保持、削除確認、ログアウトを操作しました。独立したブラウザーでBobもログイン・登録でき、互いのタスクが一覧に出ないことを確認しました。PCと幅390pxの画面も目視確認しています。ローカル検証用に作成したタスクは削除しました。

認証の自動テストには、セッション分離、同時refresh、refreshとlogoutの競合、HTTP取消時のローカル失効、CSRF、Cookie、外部URLへのリダイレクト拒否を含みます。REST試験は他人のID指定、所有者の偽装・変更、未認証・不正JWT、入力制約、公開signupの禁止も確認します。

## GitHub公開とCI

2026年10月20日、[公開リポジトリ ochtum/MiniTasks](https://github.com/ochtum/MiniTasks)へのPushと、GitHub Actions上のCI成功を確認しました。

| 対象 | 結果 |
| --- | --- |
| 対象Commit | `4a5b706689dfacaec957d75cebca434f5d6052c9` |
| GitHub ActionsのCI | [Run 39433017243](https://github.com/ochtum/MiniTasks/actions/runs/39433017243)で成功 |
| SDKの選択 | `global.json`で`version=10.0.401`、`rollForward=disable`に固定 |
| CIの成果物 | 対象Commitに対応する公開用出力とテスト結果を生成 |

初回のCIはSDKのroll-forwardに起因する問題で失敗しました。`global.json`を修正し、SDK 10.0.401を厳密に選択する設定にした後、上記のCIが成功しました。

## 初回のAzure公開

2026年10月20日、[Deploy staging Run 39437313240](https://github.com/ochtum/MiniTasks/actions/runs/39437313240)で、検査からHosted SupabaseのMigration、Azureへの配置、ヘルス確認まで成功しました。

| 対象 | 結果 |
| --- | --- |
| 対象Commit | `6009f432e9dfd3bb9056869ad98bea6eab92f915` |
| 公開URL | [MiniTasks](https://minitasks-ochtum-demo.azurewebsites.net) |
| 共通のverify | 成功 |
| AzureのOIDC認証 | 成功 |
| Hosted Migration | Session pooler経由のdry-runと未適用Migrationの反映に成功 |
| App Serviceへの配置 | 成功。LinuxのFreeプラン（F1）、Japan West、.NET 10 |
| Workflowのヘルス確認 | HTTP 200、`status=ok`、`app=MiniTasks` |
| 外部PCからのヘルス確認 | `test-health.ps1`でHTTP 200と同じJSONを確認 |
| 公開画面のブラウザー確認 | ログイン画面を表示し、利用者のログイン後にホーム画面を表示 |
| Hostedのタスク追加 | 1件を追加。成功メッセージと、全件1・未完了1を確認 |
| Hostedの完了切り替え | チェック操作後、成功メッセージと、完了1・未完了0を確認 |
| ブラウザー全体の再読込 | ログイン状態と同じ完了タスクの保持を確認 |
| HostedのDB設定 | Dashboardで`tasks`のRLS有効と、`authenticated`向けCRUDの4ポリシーを確認 |

公開用のAuth利用者も作成しました。ローカルのデモ利用者・パスワードは公開先へコピーしていません。

公開環境の操作確認で作成したタスク1件は、完了状態で利用者向けに残しています。

## 公開環境での未確認事項

Hostedでのタスク削除、絞り込み、2利用者の分離、ログアウト、通信切断を伴うBlazorの再接続は未確認です。これらはローカルや自動テストの結果と区別し、[デプロイ手順](azure-deployment.md)に従って確認します。開発・公開時間の比較値も計測していません。
