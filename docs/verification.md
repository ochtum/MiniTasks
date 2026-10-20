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

## GitHub公開とCI

2026年10月20日、[公開リポジトリ ochtum/MiniTasks](https://github.com/ochtum/MiniTasks)へのPushと、GitHub Actions上のCI成功を確認しました。

| 対象 | 結果 |
| --- | --- |
| 対象Commit | `4a5b706689dfacaec957d75cebca434f5d6052c9` |
| GitHub ActionsのCI | [Run 39433017243](https://github.com/ochtum/MiniTasks/actions/runs/39433017243)で成功 |
| SDKの選択 | `global.json`で`version=10.0.401`、`rollForward=disable`に固定 |
| CIの成果物 | 対象Commitに対応する公開用出力とテスト結果を生成 |

初回のCIはSDKのroll-forwardに起因する問題で失敗しました。`global.json`を修正し、SDK 10.0.401を厳密に選択する設定にした後、上記のCIが成功しました。

## 公開環境での未確認事項

Hosted SupabaseへのMigration、Azureへの実配置、公開URLでのヘルス確認と利用者のログイン・タスク操作は確認待ちです。CI成功はAzureでの稼働確認を含みません。これらは[デプロイ手順](azure-deployment.md)に従い、対象環境を設定して確認します。開発・公開時間の比較値も計測していません。
