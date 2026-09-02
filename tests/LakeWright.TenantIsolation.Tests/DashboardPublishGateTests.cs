using LakeWright.Embedding;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Tests for the publish gate — the safety net that refuses a dashboard whose datasets
/// do not actually filter on <c>__aibi_external_value</c>.
/// </summary>
/// <remarks>
/// <para>
/// The bypass that motivated this gate was a string-literal match. The library's
/// defense is a small tokenizer that tracks string and comment state, and only
/// reports the marker when it appears in SQL code. These tests pin the cases the
/// bypass rode through and the cases the gate still has to handle correctly.
/// </para>
/// <para>
/// The production-shape reproduction is the most important test: a board whose WHERE clause
/// mentions the marker only inside a string literal used to pass with no tenant
/// filter. The gate must refuse it. If this test ever flips green the bypass is
/// back and any tenant the board is shipped to leaks every row.
/// </para>
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class DashboardPublishGateTests
{
    [Fact]
    public void A_dashboard_whose_dataset_filters_on_the_marker_passes()
    {
        var sql = "SELECT * FROM sales WHERE __aibi_external_value = :tenant_id";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeTrue();
        verdict.Hits.Count.ShouldBe(1);
        verdict.Hits[0].DatasetIndex.ShouldBe(0);
        verdict.Hits[0].Offset.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_dashboard_whose_dataset_does_not_reference_the_marker_fails()
    {
        var sql = "SELECT * FROM sales WHERE region = 'EMEA'";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
        verdict.Reason.ShouldNotBeNull();
    }

    /// <summary>
    /// The reproduced bypass: the marker appears only inside a single-quoted string
    /// literal. A naive substring search would accept this; the gate must not.
    /// </summary>
    [Fact]
    public void A_string_literal_mentioning_the_marker_is_not_a_reference()
    {
        var sql = "SELECT * FROM sales WHERE region = '__aibi_external_value'";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_string_literal_bypass_using_string_concat_also_fails()
    {
        // The marker is split across a string concat. The gate cannot see this as
        // a reference because no bare token matches. The board is genuinely unscoped
        // and the gate correctly refuses it.
        var sql = "SELECT * FROM sales WHERE 'info: ' || '__aibi_' || 'external_value' IS NOT NULL";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_line_comment_mentioning_the_marker_is_not_a_reference()
    {
        var sql = """
            -- filter on __aibi_external_value here
            SELECT * FROM sales WHERE region = 'EMEA'
            """;

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_block_comment_mentioning_the_marker_is_not_a_reference()
    {
        var sql = """
            /* note: __aibi_external_value is the tenant claim */
            SELECT * FROM sales WHERE 1 = 1
            """;

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_real_reference_after_a_line_comment_still_counts()
    {
        var sql = """
            -- TODO: tighten this filter
            SELECT * FROM sales WHERE __aibi_external_value = :tenant_id
            """;

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeTrue();
    }

    [Fact]
    public void A_doubled_quote_escape_does_not_close_the_string()
    {
        // Standard SQL: '' inside a string is a literal single quote, not a close.
        // The gate must not let 'foo''__aibi_external_value' bleed out of the string.
        var sql = "SELECT * FROM sales WHERE note = 'it''s fine __aibi_external_value really' AND 1=0";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_marker_embedded_in_a_longer_identifier_does_not_match()
    {
        // x__aibi_external_value is not the claim column. A naive Contains would
        // match it; the gate looks for a token boundary on both sides.
        var sql = "SELECT x__aibi_external_value FROM sales";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_marker_is_matched_case_insensitively()
    {
        var sql = "SELECT * FROM sales WHERE __AIBI_EXTERNAL_VALUE = :tenant_id";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Empty_or_whitespace_sql_fails_closed()
    {
        DashboardPublishGate.Inspect(null!).Passed.ShouldBeFalse();
        DashboardPublishGate.Inspect("").Passed.ShouldBeFalse();
        DashboardPublishGate.Inspect("   \n\t  ").Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_backtick_quoted_identifier_containing_the_marker_is_not_a_reference()
    {
        var sql = "SELECT `__aibi_external_value` FROM sales";

        var verdict = DashboardPublishGate.Inspect(sql);

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Inspect_all_requires_every_dataset_to_pass()
    {
        var datasets = new[]
        {
            "SELECT * FROM a WHERE __aibi_external_value = :tenant_id",
            "SELECT * FROM b WHERE region = 'EMEA'",
        };

        var verdict = DashboardPublishGate.InspectAll(datasets);

        verdict.Passed.ShouldBeFalse();
        verdict.Reason.ShouldContain("Dataset #2");
    }

    [Fact]
    public void Inspect_all_returns_aggregated_hits_when_every_dataset_passes()
    {
        var datasets = new[]
        {
            "SELECT * FROM a WHERE __aibi_external_value = :tenant_id",
            "SELECT * FROM b WHERE __aibi_external_value = :tenant_id",
        };

        var verdict = DashboardPublishGate.InspectAll(datasets);

        verdict.Passed.ShouldBeTrue();
        verdict.Hits.Count.ShouldBe(2);
        verdict.Hits[0].DatasetIndex.ShouldBe(0);
        verdict.Hits[1].DatasetIndex.ShouldBe(1);
    }

    [Fact]
    public void Inspect_all_fails_when_there_are_no_datasets()
    {
        var verdict = DashboardPublishGate.InspectAll(Array.Empty<string>());

        verdict.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Inspect_dashboard_reports_the_name_of_each_unscoped_dataset()
    {
        var serializedDashboard = """
            {
              "datasets": [
                { "name": "orders", "queryLines": ["SELECT * FROM orders WHERE __aibi_external_value = :tenant_id"] },
                { "name": "regions", "queryLines": ["SELECT * FROM regions WHERE region = 'EMEA'"] },
                { "name": "customers", "query": "SELECT * FROM customers WHERE __aibi_external_value = :tenant_id" }
              ]
            }
            """;

        var verdict = DashboardPublishGate.InspectDashboard(serializedDashboard);

        verdict.Passed.ShouldBeFalse();
        verdict.Datasets.Count.ShouldBe(3);
        verdict.Datasets[1].Name.ShouldBe("regions");
        verdict.Datasets[1].Verdict.Passed.ShouldBeFalse();
        verdict.Datasets[2].Verdict.Passed.ShouldBeTrue();
    }

    [Fact]
    public void Inspect_dashboard_fails_closed_when_no_datasets_are_declared()
    {
        var verdict = DashboardPublishGate.InspectDashboard("""{"datasets": []}""");

        verdict.Passed.ShouldBeFalse();
        verdict.Datasets.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"datasets\":[null]}")]
    [InlineData("{\"datasets\":[42]}")]
    [InlineData("{\"datasets\":[\"not a dataset\"]}")]
    public void Inspect_dashboard_fails_closed_when_a_dataset_is_not_an_object(string serializedDashboard)
    {
        var verdict = DashboardPublishGate.InspectDashboard(serializedDashboard);

        verdict.Passed.ShouldBeFalse();
        verdict.Datasets.Count.ShouldBe(1);
        verdict.Datasets[0].Verdict.Reason.ShouldBe("Dataset is not an object.");
    }

    [Fact]
    public void Inspect_dashboard_fails_closed_when_query_lines_are_not_an_array()
    {
        var verdict = DashboardPublishGate.InspectDashboard("""{ "datasets": [{ "queryLines": "SELECT 1" }] }""");

        verdict.Passed.ShouldBeFalse();
        verdict.Datasets.Single().Verdict.Reason.ShouldBe("Dataset SQL is empty.");
    }
}
