# Azure App Serviceへ公開する

MiniTasksは、Blazor Web App（Interactive Server）をLinux App Serviceの.NET 10 Runtimeへ配置します。公開先を用意した後、GitHub Actionsの`Deploy staging`を`main`から手動実行します。この資料の追加だけでは、AzureやSupabaseの作成・設定・公開は行われません。

## 初回に用意するもの

1. Azureの有効なSubscription、Resource Group、Linux App Service PlanとWeb Appを作成します。Runtimeは`.NET 10 (LTS)`を選びます。実行時の対応状況は`az webapp list-runtimes --os linux`でも確認できます。
2. Web Appは**1インスタンス**に固定します。HTTPS Onlyを有効にし、Session affinity（ARR affinity）を有効のままにします。LinuxのWebSocketsは有効済みで、`webSocketsEnabled`の設定は適用されません。公開後に実際の対話操作と接続を確認します。プランが対応していればAlways Onを有効にすると、無通信時のアプリ停止を減らせます。
3. 公開用のSupabase Projectを作成し、Project URL、publishableキー、Project ref、DBパスワード、Session poolerのHostを確認します。HostはSupabase DashboardのConnect → Session poolerからコピーします。ローカルのProjectや認証キーと混同しないでください。
4. App Serviceの環境変数を次の表のとおり設定します。反映するとアプリが再起動します。

| App Serviceの設定名 | 値 |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true`（Linux App Serviceで必須） |
| `Supabase__Url` | 公開用Supabase ProjectのHTTPS URL |
| `Supabase__ApiKey` | 同じProjectのpublishableキー |

Linux App Serviceはフロント側のプロキシでTLSを終端します。`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`で元のHTTPSスキームをアプリへ伝え、`UseHttpsRedirection`によるリダイレクトのループを防ぎます。このフラグは転送元IPを制限しないため、信頼するApp Serviceのプロキシを経由してアプリへ接続する構成を前提とします。[ASP.NET Coreの公式設定手順](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0#forward-the-scheme-for-linux-and-non-iis-reverse-proxies)

利用者のアクセストークンやRefresh Tokenを共通設定へ登録しません。アプリは利用者のトークンでData APIを呼び、RLSで所有者の行だけを扱います。`service_role`キーやsecretキーはアプリ設定に不要です。

Hostedの認証利用者はSupabase Dashboard等で作成します。ローカルのデモ利用者・パスワード・Seedは公開先へコピーしません。メール確認やパスワード再設定を追加する場合は、Supabase AuthのSite URLとRedirect URLも公開URLに合わせます。

参考: [App Serviceの.NET 10公開手順](https://learn.microsoft.com/en-us/azure/app-service/quickstart-dotnetcore)、[Linux FAQ](https://learn.microsoft.com/en-us/troubleshoot/azure/app-service/faqs-app-service-linux-new#are-websockets-supported)、[App Serviceの設定](https://learn.microsoft.com/en-us/azure/app-service/configure-common)、[Supabase Redirect URL](https://supabase.com/docs/guides/auth/redirect-urls)。

## AzureとGitHubのOIDCを設定する

AzureのMicrosoft EntraアプリとService Principal、または対応するUser-assigned Managed Identityを作成します。GitHubのRepositoryまたはOrganizationのOIDC設定とclaim previewを確認し、実際に発行される`sub`と一致するGitHub用Federated Credentialを設定します。

GitHub.comでは、2026年7月15日以降に作成・改名・移管したRepositoryは、Owner IDとRepository IDを含むimmutable形式が既定です。それ以前のRepositoryには従来形式が残る場合があります。Repository名からSubjectを推測せず、現在の設定を確認してください。

このRepositoryの設定を読み取る例です。別のRepositoryへ配置する場合は`ochtum/MiniTasks`を置き換えます。OIDCトークンそのものを表示する必要はありません。

```powershell
gh api repos/ochtum/MiniTasks/actions/oidc/customization/sub
```

`use_default`、`use_immutable_subject`、`sub_claim_prefix`を確認します。既定の形式を使うこのWorkflowでは、prefixに`:environment:staging`を続けたSubjectを設定します。claimをカスタマイズしている場合は、preview等で確認した実際のSubjectを採用します。

| 項目 | 値 |
| --- | --- |
| Issuer | `https://token.actions.githubusercontent.com` |
| Audience | `api://AzureADTokenExchange` |
| Subject（immutable形式） | `repo:<OWNER>@<OWNER_ID>/<REPOSITORY>@<REPOSITORY_ID>:environment:staging` |
| Subject（従来形式を使うRepositoryのみ） | `repo:<OWNER>/<REPOSITORY>:environment:staging` |

`OWNER_ID`と`REPOSITORY_ID`はGitHubの数値IDです。Federated CredentialのSubjectは、IDとEnvironment名を含め、発行される`sub`と完全一致させます。[GitHubのOIDC仕様](https://docs.github.com/en/actions/reference/security/oidc#immutable-subject-claims)、[Microsoft Entraのimmutable Subject手順](https://learn.microsoft.com/en-us/entra/workload-id/workload-identities-github-immutable-subjects)、[既定形式の変更告知](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-GitHub-actions-oidc-tokens/)

Identityには、**配置先Web Appの範囲**で`Website Contributor`を付与します。Subscription全体へのContributorは、このWorkflowのためには不要です。Identity作成・Federated Credential設定・Role割当は、これらの権限を持つAzure管理者が初回に行います。

GitHubのSettings → Environmentsから`staging`を作ります。Deployment branches and tagsは`main`だけに制限します。必要な運用ではRequired reviewersも設定します。Workflow側でも`main`以外の手動実行を拒否します。

この`staging`はGitHub Environment名です。AzureのDeployment Slot名ではありません。初版は、Staging用途の通常のWeb Appを作成し、そのアプリの既定Slotへ配置します。Azureの追加Slotを作成・SwapするWorkflowは含みません。

参考: [App ServiceとGitHub Actions](https://learn.microsoft.com/en-us/azure/app-service/deploy-github-actions)、[Azure Login](https://github.com/Azure/login)、[GitHubのAzure OIDC](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)。

## GitHub Environmentへ登録する値

`staging` EnvironmentのVariablesとSecretsへ設定します。実値をYAMLやREADMEへ書きません。

| 登録場所 | 名前 | 内容 |
| --- | --- | --- |
| Variables | `AZURE_CLIENT_ID` | Azure IdentityのClient ID |
| Variables | `AZURE_TENANT_ID` | Microsoft Entra Tenant ID |
| Variables | `AZURE_SUBSCRIPTION_ID` | 配置先Subscription ID |
| Variables | `AZURE_WEBAPP_NAME` | Staging用途のWeb App名 |
| Variables | `APP_BASE_URL` | 同じWeb AppのHTTPS URL。クエリ・Fragment・認証情報は含めない |
| Variables | `SUPABASE_PROJECT_REF` | App Serviceの`Supabase__Url`と同じHosted Projectのref |
| Variables | `SUPABASE_DB_POOLER_HOST` | 同じProjectのConnect → Session poolerで表示されるHost。Host名だけを登録 |
| Secrets | `SUPABASE_DB_PASSWORD` | 同じProjectのDBパスワード |

MigrationはIPv4対応の共有Session pooler（port `5432`）へ接続します。Free Projectの直接接続はIPv6のため、GitHub runnerから到達できるSession poolerを明示します。Hostにはクラスタを識別する番号も含まれるため、リージョン名から推測せずDashboardの値を使います。

Workflowは次の形式で、パスワードを含まない接続URIを組み立てます。

```text
postgresql://postgres.<project-ref>@<session-pooler-host>:5432/postgres?sslmode=require
```

DBパスワードはSecretから環境変数`PGPASSWORD`へ渡し、URIやログには含めません。このWorkflowは管理PATと`supabase link`を使わず、既存の`SUPABASE_ACCESS_TOKEN` Secretも参照しません。Azureの配置認証には引き続きOIDCを使います。

Session poolerの接続はMigration専用です。アプリは`Supabase__Url`のHTTPS Data APIへ利用者JWTを付けてアクセスします。App ServiceにDBパスワードやpooler接続URIを登録する必要はありません。

Supabase CLIはWorkflowとローカルの両方で`2.114.0`を使用します。.NET SDKはRepositoryの`global.json`で`10.0.401`へ完全一致に固定しています。SDKの更新では、暗黙に追加されるASP.NET Coreの依存も変わる場合があるため、Lockファイルも更新してCIで確認します。CLIを更新する場合も、ローカル・CI・この手順の対応を合わせて確認してください。

参考: [Supabaseの接続方式とSession pooler](https://supabase.com/docs/guides/database/connecting-to-postgres#shared-pooler-session-mode)、[Supabaseの環境管理](https://supabase.com/docs/guides/deployment/managing-environments)、[Supabase setup-cli](https://github.com/supabase/setup-cli)、[setup-dotnet](https://github.com/actions/setup-dotnet)。

## 検査と公開の流れ

`ci.yml`はPushとPull Requestで`verify.yml`を呼びます。`deploy-staging.yml`も**同じ実行の中で**`verify.yml`を呼び、成功した場合だけ配置へ進みます。別のCI実行が以前に成功したことだけを根拠に公開しません。

1. Lockファイルで依存を復元し、Release Buildと単体テストを行います。
2. Docker上のSupabase Localを準備し、Migration、ローカルのAuth利用者を用意します。`scripts/setup-local.ps1 -SkipUserSecrets`を使います。
3. `scripts/test-supabase.ps1`でpgTAPと、実際の利用者JWTを使うREST APIの隔離テストを実行します。Hosted Projectや公開用Secretsは使いません。
4. 同じCommitのアプリを`dotnet publish`し、Commit SHA付きのArtifact名で保存します。Local設定や認証情報はArtifactへ含めません。
5. 検査成功後、`staging` Environmentを使用するdeploy jobで必要設定を確認し、今回のArtifactを取得します。
6. AzureへOIDC認証し、Project refとHostから対象ProjectのSession pooler接続URIを組み立てます。DBパスワードは`PGPASSWORD`で渡します。
7. `supabase db push --db-url "$databaseUrl" --dry-run --skip-vault --yes`で反映予定を表示し、`supabase db push --db-url "$databaseUrl" --skip-vault --yes`で未適用Migrationを反映します。`$databaseUrl`は上記のパスワードを含まないURIです。Seed投入やLocalのVault設定反映は行いません。
8. 検査済みのpublish出力をApp Serviceへ配置します。
9. `scripts/test-health.ps1`で`/healthz`がHTTP 200、JSONの`status`が`ok`、`app`が`MiniTasks`であることを確認します。Redirect先のログイン画面を成功扱いにしません。

Migrationは旧アプリでも動く追加変更を前提にします。削除・改名は新旧両方へ対応する段階を分けてください。DB反映後のアプリ配置失敗に対する一括の自動Rollbackはありません。`--dry-run`も、SQLの実行試験やRLSテストの代わりにはなりません。

Workflowの手動起動には、ファイルがGitHubの既定Branchへ存在する必要があります。Actions → Deploy staging → Run workflowで`main`を選びます。初回公開後は、ログイン、タスクのCRUD・状態変更、利用者A/Bの分離、再読込、ログアウト、Blazorの再接続を手動でも確認します。HTTPヘルス確認だけでは、この操作は検証されません。

参考: [GitHubの手動Workflow](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)、[Supabase db push](https://supabase.com/docs/reference/cli/supabase-db-push)、[webapps-deploy](https://github.com/Azure/webapps-deploy)。

## セッションとData Protectionの範囲

初版は、利用者のSupabaseセッションをサーバーのMemoryCacheに保存する学習用サンプルです。セッションの有効期間はログインから最大8時間です。認証Cookieを持っていてもサーバー内のセッションがなければ再ログインします。アプリの再起動・再配置・プロセス交換でセッションは失われます。複数インスタンスへのScale Outは対象外です。

ASP.NET Coreの認証CookieやAntiforgeryはData Protectionを使います。アプリ独自の鍵保存先は設定せず、App Serviceの既定を使用します。App ServiceはHOME配下へ鍵を永続化しますが、鍵の保存時暗号化やSlotをまたぐ鍵共有は既定では行いません。**Data Protectionの鍵の永続化と、MemoryCacheのセッションの永続化は別です。**

継続利用・複数インスタンス・Slot Swapへ拡張する際は、サーバーセッションの共有保存先と鍵管理を追加設計します。Cookieの永続化だけでRefresh Token管理、ログアウト、失効、利用者分離まで完了したとは扱いません。

参考: [Data Protectionの既定](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings?view=aspnetcore-10.0)、[Web Farmの状態管理](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/web-farm?view=aspnetcore-10.0)、[Blazorの認証](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0)。

## 失敗時に確認する箇所

| 失敗した工程 | 確認すること |
| --- | --- |
| verify | SDK・Lockファイル、Docker、Local用ポート54421系、Migration、pgTAP、利用者JWTでの隔離結果 |
| OIDC | OIDC設定・claim previewとSubjectの完全一致（immutable形式の数値IDを含む）、Environment名、IdentityのClient ID、TenantとSubscription、Web AppへのRole |
| Supabase Migration | 対象ref、Session poolerのHostとport 5432、IPv4接続、DBパスワード、適用履歴とSQL |
| App Service配置・起動 | Web App名、.NET 10 Runtime、アプリ設定、App Serviceのログ |
| health | HTTPS URL、`/healthz`が認証不要で200のJSONを返すか、起動待ちか |
| ログイン・操作 | 公開用Auth利用者、接続先Project、RLSとGRANT、セッションの期限・アプリ再起動、WebSocket接続 |

この手順とWorkflowは、設定済みの外部環境へ公開するための実装です。初回のクラウド実行結果と公開環境での確認範囲は[検証結果](verification.md)を参照してください。
