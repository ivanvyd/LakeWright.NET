# Naming & Trademark Validation â€” "Lakewright.NET" (repo `lakewright-dotnet`)

Research date: 2026-07-31. Researcher: R10-naming.
**Not legal advice.** A trademark attorney must clear any name before commercial use or before
filing anything. Several checks below are explicitly INCONCLUSIVE â€” they are marked as such and
must not be read as clearance.

## Method note on evidence quality

Where a registry offered a machine-readable API, I used it rather than the HTML site, because the
HTML pages are JS-rendered and the fetcher returns misleading partial content. Concretely:

- `https://www.nuget.org/packages?q=Lakewright` (HTML) rendered a **false positive** â€”
  it reported `Microsoft.Azure.Management.DataFactory` as a result. That is an artifact of the
  fetch, not a real match. I therefore used the NuGet search API and the flat-container API.
- For domains I used **RDAP** (the successor to WHOIS), and I validated every RDAP endpoint with a
  known-registered control domain before trusting a 404. This matters: my first `.dev` endpoint
  (`www.registry.google/rdap/`) 404'd on *everything*, including the control. Without the control
  I would have wrongly reported `lakewright.dev` as free from a broken endpoint.

---

## 1. NuGet

### Is the `Lakewright` prefix taken? â€” NO, it is free

| Probe | URL | Result |
|---|---|---|
| Search API, all terms | `https://azuresearch-usnc.nuget.org/query?q=Lakewright&prerelease=true` | `totalHits: 1`, and the single hit is `Microsoft.Azure.Management.DataFactory` â€” a fuzzy token match on "lake"/"data", **not** a `Lakewright*` package |
| Exact package ID | `https://azuresearch-usnc.nuget.org/query?q=PackageId:Lakewright.Core&prerelease=true` | `totalHits: 0` |
| Direct package page | `https://www.nuget.org/packages/Lakewright.Core` | **HTTP 404** |
| Flat container (authoritative existence check) | `https://api.nuget.org/v3-flatcontainer/lakewright/index.json` | **HTTP 404** â€” package ID `lakewright` does not exist |

**Conclusion: no package under the `Lakewright` prefix exists.** Confidence: HIGH. This is a genuine
404 on the authoritative flat-container endpoint plus a zero-hit exact-ID query, which is the
standard used in the brief ("do not claim available unless you saw a 404 / no-results").

### NuGet ID prefix reservation policy

Policy URL: <https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation>

**How to apply** (quoted from the policy):

> 1. Review the acceptance criteria for prefix ID reservation.
> 2. Determine the prefixes you want to reserve, in addition to any advanced prefix reservation
>    scenarios you may require.
> 3. Send a mail to account@nuget.org with the owner display name on nuget.org, as well as any
>    reserved prefixes you are requesting.

**Acceptance criteria** (quoted verbatim):

> 1. Does the package ID prefix properly and clearly identify the reservation owner?
> 2. Is the package ID prefix something common that should not belong to any individual owner or
>    organization? Avoid ID prefix reservations that are shorter than four characters and avoid
>    common or generic words.
> 3. Would *not* reserving the package ID prefix cause ambiguity, confusion, or other harm to the
>    community?

Note the policy also says: "not all criteria need to be met for a prefix to be reserved, but the
application may be denied if there is not substantial evidence of the criteria being met".

**Publishing best practices required alongside a reservation** (quoted):

> 1. Are the identifying properties of the packages that match the package ID prefix clear and
>    consistent (especially the package author)?
> 2. Do the packages have a license (using the license metadata element and NOT licenseUrl which is
>    being deprecated)?
> 3. If the packages have an icon (using the iconUrl metadata element), are they also using the
>    icon metadata element? ... embedded icons must be used.

**Practical read for this project.** Criterion 1 is the hard one for a brand-new project: the
prefix must "clearly identify the reservation owner", and a name nobody has heard of yet identifies
nobody. Realistically, reserve *after* publishing a few packages with consistent author metadata,
an SPDX `license` element and an embedded `icon`. Also relevant: the policy supports marking a
prefix **public**, which is designed for exactly this case â€”

> This is useful for open source projects with many contributors - the top or core contributors can
> have the prefix reserved, but it can still be open to all contributors.

There is also a dispute path: prefixes that "infringe on any trademarks or copyrights" can be
challenged at support@nuget.org. That cuts both ways â€” it is a reason to make sure the chosen name
is not someone else's mark.

### Precedent: how third parties name Databricks-related NuGet packages

Query: `https://azuresearch-usnc.nuget.org/query?q=Databricks&prerelease=true&take=30` â†’ 34 hits.
Every package puts the **publisher's own brand first** and uses "Databricks" only as a descriptor:

- `Microsoft.Azure.Databricks.Client` (owner: Microsoft)
- `HashiCorp.Cdktf.Providers.Databricks` (owner: hashicorp)
- `Pulumi.Databricks` (owner: pulumi-bot)
- `CData.Databricks` (owner: CDataSoftware)
- `Energinet.DataHub.Core.Databricks.SqlStatementExecution` (owner: GreenEnergyHub)
- `Apache.Arrow.Adbc.Drivers.Databricks` (owner: lidavidm)
- `Storage.Net.Microsoft.Azure.Databricks.Dbfs` (owner: aloneguid)

**No package is named `Databricks.*`.** That prefix is conspicuously untouched. This is the safe,
observed convention: `<YourBrand>.Databricks.<Thing>`, never `Databricks.<Thing>`.

---

## 2. GitHub

| Probe | URL | Result |
|---|---|---|
| Org/user `lakewright` | `https://github.com/lakewright` | **HTTP 404** â€” does not exist |
| Repo search, all fields | `https://api.github.com/search/repositories?q=lakewright` | `total_count: 0` |
| Repo search, name+desc+readme | `https://api.github.com/search/repositories?q=lakewright+in:name,description,readme` | `total_count: 0` |

**Conclusion: `lakewright` org and `lakewright` / `lakewright-dotnet` repo names are unused.**
Confidence: HIGH.

---

## 3. Domains

Method: RDAP (RFC 7482). A `404` from a *validated* RDAP endpoint means "domain not found in the
registry" = unregistered. Each endpoint was validated with a control domain first.

| Domain | Endpoint | Control (must return data) | Result |
|---|---|---|---|
| `lakewright.com` | `rdap.verisign.com/com/v1/domain/` | `databricks.com` â†’ registered, Amazon Registrar, 2011-07-13 | **404 â†’ available** |
| `lakewright.net` | `rdap.verisign.com/net/v1/domain/` | `databricks.net` â†’ registered, MarkMonitor, 2019-10-21 | **404 â†’ available** |
| `lakewright.io` | `rdap.identitydigital.services/rdap/domain/` | `github.io` â†’ registered, MarkMonitor, 2013-03-08 | **404 â†’ available** |
| `lakewright.dev` | `pubapi.registry.google/rdap/domain/` | `web.dev` â†’ registered, MarkMonitor, 2018-10-29 | **404 â†’ available** |

**Conclusion: all four of `.com`, `.net`, `.io`, `.dev` appear unregistered.** Confidence: HIGH for
"not currently registered". Caveats that keep this short of a guarantee: RDAP does not show
registry-reserved / premium-tier names, and availability can change hour to hour. Confirm at a
registrar at purchase time.

Also checked for the leading alternative:

| Domain | Result |
|---|---|
| `laketenant.com` | **404 â†’ available** (Verisign RDAP, control validated) |
| `laketenant.dev` | **404 â†’ available** (Google RDAP, control validated) |
| `lakestack.dev` | **404 â†’ available** (Google RDAP, control validated) â€” but see Â§7, the *name* is taken commercially |

---

## 4. Trademark

### "LAKEWRIGHT" as a mark â€” INCONCLUSIVE (but no positive signal of a conflict)

I could **not** query the trademark registers directly. Recording the failures honestly:

| Source | Attempt | Outcome |
|---|---|---|
| Justia Trademarks search | `https://trademarks.justia.com/search?q=lakewright` | **HTTP 403 Forbidden** â€” bot-blocked |
| Justia, Databricks owner page | `https://trademarks.justia.com/owners/databricks-inc-3568395/` | **HTTP 403 Forbidden** |
| uspto.report search | `https://uspto.report/Search/?q=lakewright` | **HTTP 403 Forbidden** |
| uspto.report company page | `https://uspto.report/company/Databricks-Inc` | **HTTP 403 Forbidden** |
| USPTO TMSearch API | `https://tmsearch.uspto.gov/api-v1-0-0/tmsearch?q=lakewright` | **HTTP 404** â€” not a public GET endpoint |
| TrademarkElite | `https://www.trademarkelite.com/trademark/search?q=lakewright` | **HTTP 404** |
| EUIPO eSearch / TMview | â€” | JS-rendered SPA / POST-only API, not fetchable |

**USPTO TESS/TMSearch and EUIPO were NOT searched. This check is INCONCLUSIVE.** Nobody should
treat the absence of a hit below as clearance.

Indirect (weak, negative) signal only: an indexed search restricted to `trademarks.justia.com` for
`LAKEWRIGHT`/`LAKE SAAS` surfaced no such mark (it surfaced an unrelated `LAKES`, serial 87666069),
and a general web search for `"lakewright"` returned zero references to any product, company or
filing. For a registered mark in class 9/42 you would normally expect *some* indexed footprint.
That is suggestive of "no existing LAKEWRIGHT mark", nothing more.

### Conflicting marks containing "LAKE" in software/data classes

Verified:

- **DATABRICKS** is a registered mark of Databricks, Inc. â€” Reg. No. 5003730, Serial No. 86066214,
  filed Sept 2013, covering computer software for big data analysis and related services.
  <https://trademarks.justia.com/860/66/databricks-86066214.html> (via indexed search; page itself
  403s). A second filing appears at serial 88861287 and a recent one at 99192548.
- **DELTA ENGINE** â€” Databricks, Inc., Reg. No. 6611758, Serial No. 90478105.
  <https://trademarks.justia.com/904/78/delta-90478105.html>
- **DATALAKEHOUSE** â€” Reg. No. 6639985, Serial No. 88841148, registered 2022-02-08.
  **Owned by a third party, not Databricks** (DataLakeHouse.io).
  <https://trademark.justia.com/888/41/datalakehouse-88841148.html>

### Is "Lakehouse" claimed by Databricks as a mark? â€” No evidence found

Databricks unquestionably *coined and popularised* the category term â€” see Scale Venture Partners'
write-up, "Naming the Lakehouse: How Databricks created their category"
(<https://www.scalevp.com/blog/naming-the-lakehouse-how-databricks-created-their-category>) â€” but I
found **no registered LAKEHOUSE mark owned by Databricks**. The nearest registration, DATALAKEHOUSE,
belongs to someone else. Databricks' own usage is descriptive/category-building ("Databricks
Lakehouse Platform"), and the term is now used freely by AWS, Google, Microsoft Fabric, Dremio,
Snowflake and others. Practical read: **"lakehouse" behaves as a generic industry term**, low risk
as a descriptor, and correspondingly **weak as a distinctive brand element**. Confidence: MEDIUM
(register not directly searched).

### "SaaS" genericness

"SaaS" is a purely generic industry acronym. It contributes essentially **zero** trademark
distinctiveness. In a composite mark it is treated as descriptive matter and would typically be
disclaimed. Consequence for `Lakewright`: the only distinctive element is "Lake" â€” which is itself
weak and crowded in the data space (Data Lake, Lake Formation, LakeSail, Lakehouse, LakeFS,
Lakeside Software). **A `Lake` + generic-suffix name is a weak mark**: hard to register, hard to
enforce, and easy to collide with. This is a brand-strategy problem more than a legal-risk problem.

---

## 5. Databricks trademark policy for third-party projects

### There is no dedicated public "Databricks Trademark Policy for OSS" page

I enumerated `https://www.databricks.com/legal` in full. The page lists MCSA, AUP, External User
Terms, US Public Sector, Free Edition ToS, Partner T&Cs, Website ToU, Event Terms, Usage Commit
Terms, Additional Billing Terms, Procurement MSA, DPA, SCC, EU Data Act Addendum, Privacy Notice,
Subprocessors, Cookie Notice, Code of Conduct, Third Party CoC, Modern Slavery Statement.
**No trademark policy, brand policy, or trademark usage guidelines link exists on that page.**
`https://www.databricks.com/legal/trademark-policy` and `.../legal/trademarks` both return **404**.

This is a meaningful finding in itself: unlike Apache, Linux Foundation, Rust or Mozilla, Databricks
publishes **no OSS-facing trademark permission**. There is no "you may say 'for Databricks'" grant
to rely on. Absent an express policy, third-party use falls back on general nominative-fair-use
doctrine, which is a legal argument rather than a permission.

### What Databricks *does* say

**Website Terms of Use** (<https://www.databricks.com/legal/terms-of-use>), Â§2 License Grant and
Proprietary Rights:

> "Apache" and "Spark" are trademarks of the Apache Software Foundation. Any other third party
> trademarks, service marks, logos, trade names or other proprietary designations, that are or may
> become present within the Sites, including within any Content, are the registered or unregistered
> trademarks of the respective parties.

and users may not

> remove any copyright, trademark or other proprietary rights notice from the Sites or from Content
> or other materials contained on or originating from the Sites.

**Databricks Open Model License** (<https://www.databricks.com/legal/open-model-license>) â€” the
Apache-style trademark carve-out:

> No trademark licenses are granted under this Agreement, and in connection with the DBRX or DBRX
> Derivatives, neither Databricks Inc. nor licensee may use any name or mark owned by or associated
> with the other or any of its affiliates, except as required for reasonable and customary use in
> describing and redistributing the DBRX or DBRX Derivatives.

The same "reasonable and customary use in describing the origin of the work" carve-out appears in
the Apache-2.0 Â§6 that governs Databricks' OSS repos (e.g. `databricks/dbt-databricks`,
<https://github.com/databricks/dbt-databricks/blob/main/License.md>).

**Partner Program Terms & Conditions** (<https://databricks.com/partnertcs>; PDF mirror at
<https://www.databricks.com/kr/wp-content/uploads/2022/07/Databricks-Partner-Program-TCs_11July2022.pdf>).
The PDF would not render to text through the fetcher, so the following is from the indexed summary,
**paraphrase not verbatim quote** â€” verify before relying on it:

- Any use of a Databricks Mark by a Partner must correctly attribute ownership to Databricks and
  comply with Databricks' then-current trademark usage guidelines.
- Goodwill from Partner's use inures solely to Databricks.
- Partners "will not contest or aid in contesting the validity or ownership of any Databricks Mark
  ... including applying to register any trademark that is confusingly similar to any Databricks
  Mark."
- Databricks may withdraw approval of any use at any time in its sole discretion.
- Use must not cause confusion about the relationship or ownership of products/solutions.

Note this binds **Partners** under the partner agreement. It does not directly bind an unaffiliated
OSS project â€” but it tells you what Databricks' posture is.

**Brand assets.** Brand guidelines live at `https://brand.databricks.com/` (terms at
`/terms-and-conditions`) and Brandfolder
(`https://brandguides.brandfolder.com/databricks-extended-brand-guidelines/co-branding`). Both are
JS-rendered and **could not be fetched as text â€” the operative sentences are UNVERIFIED**. The
public press kit (<https://www.databricks.com/company/newsroom/press-kit>) offers logos with
"typography and usage guidelines" and routes asset/trademark questions to **press@databricks.com**;
partner branding questions go to **partner-marketing@databricks.com**.

### Answers to the two direct questions

**Can we say "for Databricks"?** â€” Most likely yes, as *descriptive, non-prominent* text, e.g.
"an open-source .NET accelerator for building multi-tenant SaaS on Databricks". Basis: the
Apache-2.0 / Open Model License carve-out for "reasonable and customary use in describing" the
work, plus ordinary nominative fair use. Constraints that follow from Databricks' own partner
language: keep it factual, keep "Databricks" visually subordinate to your own name, never imply
endorsement or partnership, and add an attribution line
("Databricks is a trademark of Databricks, Inc. This project is not affiliated with or endorsed by
Databricks, Inc."). **Confidence: MEDIUM â€” this is inference from adjacent documents, not an
express public grant, because no such grant is published.**

**Can we use their logo?** â€” **No.** Do not. There is no public license to do so, brand assets are
gated behind partner/press channels, and the extended brand guidelines are approval-based. Using the
Databricks logo in a README, site or package icon without written permission is the single clearest
avoidable risk here. Use plain word-mark text instead.

**Should "Databricks" appear in the project name?** â€” **No.** Zero of the 34 Databricks-related
NuGet packages use a `Databricks.*` prefix, and the partner terms prohibit registering confusingly
similar marks. Put your own brand first.

---

## 6. The ".NET" suffix

### The letter of the rule

`.NET` is a Microsoft trademark. Microsoft's **Trademark and Brand Guidelines**
(<https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks>) say, verbatim:

> Don't use Microsoft's Brand Assets in the name of your business, product, service, app, domain
> name, social media account, other offering, or business indicator.

with cited violation examples "Contoso OneDrive software" and "www.azurevirtualmachine.com". And:

> Unless you have an express license from Microsoft, these Trademark Guidelines will exclusively
> govern your use of our Brand Assets.

> Our logos, app and product icons, illustrations, photographs, videos, and designs can never be
> used without an express license.

What **is** permitted:

> Note if your product, service, or solution is interoperable or compatible with a Microsoft
> product, service, or solution.

â€” e.g. "Contoso software works with Microsoft OneDrive" â€” and such use must be "less prominently
than your own brand or company name unless you have a strategic partnership agreement."

The **.NET Foundation** publishes no separate public policy on the `.NET` suffix.
`https://dotnetfoundation.org/legal` and `.../legal/logo-usage` both **404**. The `dotnet/brand`
repo (<https://github.com/dotnet/brand>) contains only `README.md`, `LICENSE` (CC0-1.0),
`dotnet-styleGuide-2024.pdf` and asset folders. The README says the brand guidelines and logo "are
copyright of the .NET authors"; the CC0 LICENSE explicitly reserves marks â€”
"No trademark or patent rights held by Affirmer are waived". The 15 MB style-guide PDF exceeded the
fetch size limit, so **its contents are UNVERIFIED**. `dotnet/brand` issue #10, "Clarify Brand
Guidelines for .NET Foundation and Community Usages", asks precisely this question and **has no
official reply** â€” the ambiguity is real and acknowledged.

### The practice

The `<Name>.NET` / `<Name>.Net` pattern is pervasive and, as far as any public record shows,
unenforced against OSS libraries:

- **`WireMock.Net`** â€” owner `sheyenrath` (an individual, not a company), **50M+ downloads**,
  plus `WireMock.Net.Abstractions`, `.OpenApiParser`, `.Minimal`. Verified live on NuGet.
- **`Elasticsearch.Net`** â€” owners `elastic, elasticsearch, ...` (the vendor itself).
  Also `Elasticsearch.Net.Aws` (third party `bcuff`), `Elasticsearch.Net.VirtualizedCluster`.

### Verdict on the suffix

The pattern is **acceptable in practice**, with conditions:

1. **`.NET` must be the suffix/qualifier, never the distinctive element.** `Lakewright.NET` is fine in
   form; `NET Lakehouse` would not be.
2. **Your own brand leads and dominates.** This aligns with the "less prominently than your own
   brand" allowance.
3. **Never use the .NET logo, the purple `.NET` bug, or Microsoft logos** without an express
   licence â€” that prohibition is unambiguous and absolute in the guidelines.
4. **Don't imply Microsoft authorship, endorsement, or that you are part of .NET itself.**
5. Prefer the low-friction convention: use `.NET` in the human-readable project name and README
   prose, and let the NuGet IDs be plain (`Lakewright.Core`, not `Lakewright.NET.Core`).

Residual risk: **LOW but non-zero.** It is technically outside the letter of the guidelines, and
Microsoft reserves the right to object. The realistic downside is a polite rename request years
out, not litigation. Confidence: MEDIUM-HIGH on practice, HIGH on the quoted rule text.

---

## 7. Name confusion and communication

### Collisions

| Check | Result |
|---|---|
| Web search `"lakewright"` | **No company, product, or project of that name.** Results are unrelated (Lake Products Company, Land O'Lakes, `fingerlakesaa.org`, `lake-sea.com`) |
| GitHub | 0 repos, org free |
| NuGet | 0 packages |

**Direct collision risk: essentially nil.** Confidence: HIGH.

Adjacent names in the same market that make the *space* crowded (none is a blocking collision, but
each dilutes searchability and each is a "Lakeâ€¦" data company):

- **LakeSail** â€” SF startup, Rust-native Spark replacement.
  <https://www.futuriom.com/articles/news/lakesail-rewrites-big-data-processing/2026/03>
- **Lakeside Software** â€” established DEX vendor. <https://en.wikipedia.org/wiki/Lakeside_Software>
- **DataLakeHouse.io** â€” owns the DATALAKEHOUSE registration.
- **AWS Lake Formation**, **LakeFS**, **Delta Lake**, **databrickslabs/lakebridge**.

### Does the name communicate the value?

**No â€” and this is the strongest argument against it.** Three problems:

1. **It describes the wrong thing.** "Lakewright" reads as *"a SaaS product that is a lake"* â€” i.e.
   a hosted lakehouse offering. The actual product is a **toolkit for building your own SaaS on
   someone else's lakehouse**. The name inverts the value proposition. Anyone landing on the repo
   expects a managed service and finds a library. (Compare: LakeStack, below, genuinely *is* the
   hosted-lakehouse product the name implies.)
2. **It is hard to say and hard to type.** "Lake-Sass"? "Lake-Ess-Ay-Ay-Ess"? The internal
   capitalisation `Lakewright` is unstable in the wild â€” people will write `LakeSaas`, `Lakesaas`,
   `LakeSAAS`. That fragments search, package IDs and the repo slug (`lakewright-dotnet` vs the
   `Lakewright.*` package prefix is already an inconsistency you'd carry forever).
3. **"SaaS" dates the project and adds nothing.** It is a generic, faintly 2015-flavoured acronym
   that carries no distinctiveness (see Â§4) and tells a .NET developer nothing they wanted to know.
   The thing they care about â€” *multi-tenancy* â€” is the word that is missing.

There is no Databricks-endorsement problem with the name (it doesn't reference Databricks at all),
which is a genuine point in its favour.

---

## 8. Alternatives

All checked on NuGet (search API), GitHub (search API), and a web search. **Four candidates were
eliminated by these checks** â€” worth recording, because three of them are names one would otherwise
have reached for.

### Eliminated

| Name | Killed by | Evidence |
|---|---|---|
| **LakeStack** | **Live commercial product in the exact same category** | LakeStack is an AWS-native no-code data lakehouse SaaS, listed on AWS Marketplace across manufacturing / automotive / legal verticals, with a G2 competitor page and site `lakestackbyapplify.co`. NuGet and GitHub were clear, and `lakestack.dev` is free â€” which is exactly why registry checks alone are not enough. |
| **Tenantry** | **Taken on NuGet by a direct competitor** | `Tenantry.Core`, `Tenantry.AspNetCore`, `Tenantry.EfCore` all live â€” an existing .NET multi-tenancy library (ITenantContext, tenant resolvers, EF Core interceptor isolation). Plus `gettenantry/tenantry`, `tenantry-org/tenantry-core`, `tenantry-org/tenantry.dev` on GitHub. **This is also a competitor finding, not just a naming one.** |
| **Lakeform** | Collides with AWS Lake Formation | GitHub `q=lakeform` â†’ 69 repos, dominated by `aws/aws-lakeformation-best-practices`, `cloudposse/terraform-aws-lakeformation`, etc. |
| **Lakebridge** | It is a Databricks Labs project | `databrickslabs/lakebridge` (153â˜…) â€” "Accelerates migrations to Databricks". Using it would imply Databricks affiliation. |
| **Alluvial** | Crowded, incl. an existing C# project | 154 GitHub repos; `jonsequitur/Alluvial` is a C# data-streaming library. |
| **Quayside** | Crowded | 20 repos incl. `quayside-app/quayside` (quayside.app) and `quayside-dev/quayside` (quayside.dev). |
| **Bricklayer** | Occupied in the Databricks Python space | `intelematics/bricklayer` â€” "common libs for commonly used python modules in Databricks". NuGet was clear (0 hits) but the mindshare is taken. |

### Ranked shortlist

| # | Name | NuGet | GitHub | Web | Pro / Con |
|---|---|---|---|---|---|
| 1 | **LakeTenant** (`LakeTenant.NET`) | 0 hits | 0 repos (`in:name`) | no product found | **Pro:** says exactly what it is â€” multi-tenancy on a lakehouse; the missing word in Lakewright. Fully clean across every register; `laketenant.com` **and** `laketenant.dev` both free. **Con:** slightly utilitarian, "tenant" is jargon to non-SaaS devs. |
| 2 | **Lakewright** (`Lakewright.NET`) | 0 hits | 0 repos | no product found | **Pro:** completely clean, genuinely brandable; "-wright" (shipwright, wheelwright) = *one who builds*, which is precisely the accelerator's job. Distinctive enough to be a real mark, unlike Lake+generic. **Con:** doesn't self-describe â€” needs a tagline to land; risk of being misspelled "Lakeright". |
| 3 | **TenantLake** | not separately probed | 0 repos (`in:name`) | no product found | **Pro:** same clarity as #1, clean. **Con:** strictly worse than LakeTenant â€” reads as "a lake of tenants"; NuGet not independently confirmed (infer LOW risk from the zero-hit `Tenant`/`Lake` searches, but **verify before committing**). |
| 4 | **Lakehouse.NET** / **LakehouseKit** | 0 hits for both | â€” | category term, widely used | **Pro:** maximal SEO and instant comprehension for the target audience. **Con:** generic â€” near-zero trademark distinctiveness, unenforceable, and you compete for search results with every vendor's lakehouse marketing. Also mild (unverified) risk if anyone ever asserts LAKEHOUSE. |
| 5 | **LakeVault** | 0 hits | 1 repo: `SinaVosooghi/LakeVault` â€” "governed Lakehouse platform on Azure" | no product found | **Pro:** clean on NuGet, evokes governance/isolation. **Con:** an existing same-domain repo, and "Vault" is strongly owned by HashiCorp Vault in dev mindshare â€” implies secrets management, which this isn't. |
| 6 | **Lakeworks** | 0 hits | GitHub **user `lakeworks` is taken** (7 repos, unrelated IIS/compression work) | no product found | **Pro:** "works" nicely implies a toolkit; NuGet clear. **Con:** the org handle is gone, so you'd need `lakeworks-dotnet` or similar â€” a permanent papercut. |
| 7 | **LakehouseKit** | 0 hits | â€” | â€” | **Pro:** "Kit" correctly signals accelerator/toolkit rather than a service â€” fixes Lakewright's core misdirection. **Con:** inherits "lakehouse" genericness; GitHub not separately probed. |
| 8 | **Lakewright** (incumbent) | 0 hits | 0 repos, org free | no collisions | **Pro:** every register is clean, all four TLDs free. **Con:** misdescribes the product as a SaaS rather than a SaaS-building toolkit; awkward casing and pronunciation; "SaaS" adds no distinctiveness. |

**Not yet checked for the shortlist:** domains for #2â€“#7 (only LakeTenant's were verified), and
USPTO/EUIPO for *all* names. Both must happen before anything is locked in.

---

## 9. Verdict

**CHANGE â€” but not urgently, and not for legal reasons.**

`Lakewright.NET` is *legally and practically usable*. Every availability check came back clean:
NuGet prefix free (404 on the authoritative endpoint), GitHub org and repo names free, all four
domains unregistered, no company or product of that name anywhere on the web, no Databricks
reference in the name at all. There is no blocking conflict. If the team wants to ship under it
today, nothing stops them, and the `.NET` suffix follows a pattern that WireMock.Net has ridden to
50M+ downloads unchallenged.

The reason to change is **positioning**: the name says the product *is* a SaaS when it is a toolkit
for *building* one, and "SaaS" contributes no distinctiveness while making the name hard to
pronounce and inconsistently capitalised. That cost compounds with every README, talk and package
ID. **`LakeTenant.NET`** fixes it â€” same clean-across-the-board status, plus `.com` and `.dev` both
free, and it names the actual differentiator (multi-tenancy). **`Lakewright.NET`** is the stronger
choice if the team wants something brandable and defensible as a real mark rather than descriptive.

Renaming is cheap now and expensive after the first release. This is the moment to decide.

### Required actions regardless of the name chosen

1. **Do not use the Databricks logo** anywhere â€” README, docs site, package icon, slides. No public
   licence exists for it.
2. **Do not put "Databricks" in the project or package name.** Follow the observed convention:
   `<YourBrand>.Databricks.<Thing>` if a Databricks-specific package is ever needed.
3. **Add a disclaimer** to the README and the docs site footer:
   *"Databricks is a trademark of Databricks, Inc. This project is not affiliated with, endorsed
   by, or sponsored by Databricks, Inc."* Do the same for .NET/Microsoft.
4. **Describe, don't brand:** "for Databricks" / "works with Databricks" in body text, with
   "Databricks" less prominent than your own name. Never in a logo lockup.
5. **Get a trademark search done properly** â€” USPTO and EUIPO classes 9 and 42 â€” by a lawyer, before
   any commercial use, any funding conversation, or any filing. **Nothing in this document is a
   clearance search; Â§4 is explicitly INCONCLUSIVE.**
6. **Defer NuGet prefix reservation** until a few packages are published with consistent author
   metadata, an SPDX `license` element and an embedded `icon`; then apply to account@nuget.org and
   consider requesting the prefix be marked **public** so contributors can publish under it.
7. **Register the domains early** (`.com` + `.dev` at minimum) â€” they are free today and cost far
   less than a rename later.

## 10. Confidence summary

| Check | Verdict | Confidence | Why |
|---|---|---|---|
| NuGet prefix free | Free | HIGH | 404 on flat-container + 0 hits exact-ID |
| NuGet prefix policy | Documented | HIGH | Primary source, quoted verbatim |
| GitHub org/repo free | Free | HIGH | 404 + `total_count: 0` on two queries |
| Domains .com/.net/.io/.dev | Unregistered | HIGH | RDAP 404 with validated controls |
| USPTO / EUIPO for LAKEWRIGHT | **INCONCLUSIVE** | â€” | All registers 403/404; not searched |
| Databricks marks exist | Confirmed | MEDIUM-HIGH | Reg. 5003730 etc. via indexed sources |
| "Lakehouse" claimed by Databricks | No evidence of a mark | MEDIUM | Register not directly searched |
| Databricks OSS trademark policy | **No public policy exists** | HIGH | Full `/legal` index enumerated; 404s |
| Databricks logo usable | **No** | HIGH | No public grant; gated behind partner/press |
| "for Databricks" permissible | Likely yes | MEDIUM | Inferred from licence carve-outs, not an express grant |
| Databricks brand guidelines text | **UNVERIFIED** | â€” | JS-rendered; not fetchable |
| `.NET` suffix rule (letter) | Prohibited on paper | HIGH | Microsoft guidelines quoted verbatim |
| `.NET` suffix (practice) | Widely tolerated | MEDIUM-HIGH | WireMock.Net 50M+ dl, Elasticsearch.Net |
| .NET Foundation `.NET` policy | None published | HIGH | 404s; `dotnet/brand` #10 unanswered |
| .NET style-guide PDF contents | **UNVERIFIED** | â€” | 15 MB, exceeded fetch limit |
| Lakewright name collision | None | HIGH | Zero hits across NuGet/GitHub/web |
| Alternatives screening | 7 eliminated, 3 clean | MEDIUM-HIGH | Registry APIs + web search per name |
