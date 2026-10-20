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

実ブラウザーでは、Aliceのログイン、タスク追加、完了切り替え、絞り込み、再読込後の状態保持、削除確認、ログアウトを操作しました。独立したブラウザーでBobもログイン・登録でき、互いのタスクが一覧に出ないことを確認しました。PCと幅390pxの画面も目視確認しています。ブラウザー検証用に作成したタスクは削除しました。

認証の自動テストには、セッション分離、同時refresh、refreshとlogoutの競合、HTTP取消時のローカル失効、CSRF、Cookie、外部URLへのリダイレクト拒否を含みます。REST試験は他人のID指定、所有者の偽装・変更、未認証・不正JWT、入力制約、公開signupの禁止も確認します。

Azureリソース作成、GitHubへのPush、GitHub Actions上の実行、Hosted ProjectへのMigration、Azureへの実配置は行っていません。これらは[デプロイ手順](azure-deployment.md)に従い、対象環境を設定して確認する範囲です。開発・公開時間の比較値も計測していません。
