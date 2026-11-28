# Databricks platform premises, 2026-11-28

What the Databricks documentation said on 2026-11-28 about the premises of
[ADR 0001](../decisions/0001-host-the-application-outside-databricks.md) and
[When not to use LakeWright](../guides/when-not-to-use-lakewright.md). The four quotes marked
"re-checked" were fetched a second time and matched verbatim. The others are as one research pass
recorded them.

| Premise | Quote | Source | Check |
|---|---|---|---|
| Apps cannot be public | "You can't make Databricks apps public." "Anonymous access and bypassing single sign-on (SSO) are not supported." | [Apps permissions](https://docs.databricks.com/aws/en/dev-tools/databricks-apps/permissions) | re-checked |
| Apps users are account identities | "Users don't need to belong to the same workspace, but they must be part of the same Azure Databricks account." | [Apps key concepts (Azure)](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/key-concepts) | recorded |
| An app's address is fixed | "You can't change the URL after you create the app." Account-level Custom URLs are in Public Preview and give "a single, branded domain like mycompany.databricks.com." | [Apps key concepts (Azure)](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/databricks-apps/key-concepts), [Custom URLs announcement](https://www.databricks.com/blog/announcing-public-preview-custom-urls) | recorded |
| Apps compute | XLarge: "Up to 12 vCPUs", 48 GB. "The `XLarge` size is available only in some regions." | [Apps compute size](https://docs.databricks.com/aws/en/dev-tools/databricks-apps/compute-size) | re-checked |
| Apps scaling | "Horizontal scaling for Databricks Apps is now generally available" (September 29, 2026). One to five instances per app. | [Release notes, September 2026](https://docs.databricks.com/aws/en/release-notes/product/2026/september) | re-checked |
| Apps runtime | "Python 3.11, running in a dedicated virtual environment" and "Node.js version 22." No .NET or custom container is documented. | [Apps system environment](https://docs.databricks.com/aws/en/dev-tools/databricks-apps/system-env) | recorded |
| Apps are not for customer-facing use | "Public-facing or external customer applications, which require different hosting architectures." | [Databricks developer hub](https://developers.databricks.com/perspectives/what-is-the-best-way-to-deploy-an-internal-data-app-without-setting-up-separate-hosting-and-authentication-infrastructure) | recorded |
| External embedding exists | "This approach lets you share dashboards with viewers outside of your organization, such as partners and customers, without provisioning Databricks accounts for those users." | [External embedding](https://docs.databricks.com/aws/en/dashboards/share/embedding/external-embed) | re-checked |
| External embedding is generally available | "Dashboard embedding for external users is generally available" (March 2026). | [AI/BI release notes 2026](https://docs.databricks.com/aws/en/ai-bi/release-notes/2026) | recorded |
| External embedding limits | "The **Ask Genie** button is not supported in embedding for external users." "External embedding has a rate limit of 20 dashboard loads per second." | [External embedding](https://docs.databricks.com/aws/en/dashboards/share/embedding/external-embed) | recorded |
| Lakebase | "Lakebase is a fully managed Postgres database integrated into the Databricks platform." Generally available on AWS (January 22, 2026), Azure (March 2, 2026) and Google Cloud (October 30, 2026). | [Lakebase](https://docs.databricks.com/aws/en/oltp/) and its release notes | recorded |
| SDK languages | Python, JavaScript, Go and Java are listed; the R SDK is a Labs project. No .NET SDK is listed. | [Databricks SDKs](https://docs.databricks.com/aws/en/dev-tools/sdks) | recorded |
| App templates | Python and Node.js templates only. | [databricks/app-templates](https://github.com/databricks/app-templates) | recorded |
