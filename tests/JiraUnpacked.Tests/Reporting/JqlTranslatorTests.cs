using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Jql;
using static JiraUnpacked.Tests.Reporting.JqlTestKit;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>The translator's tree walk, sort, the by-name fields, key, summary and parent — pure, no database.</summary>
public sealed class JqlTranslatorTests
{
    [Fact]
    public void No_filter_is_the_whole_project_in_the_lists_default_order()
    {
        var t = Translate(null);
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", t.Jql);
        Assert.Empty(t.Warnings);
    }

    [Fact]
    public void The_sort_becomes_order_by_with_key_as_the_tiebreaker()
    {
        Assert.EndsWith(" ORDER BY priority ASC, key ASC", Translate(null, sortBy: "priority", sortDescending: false).Jql);
        Assert.EndsWith(" ORDER BY created DESC, key ASC", Translate(null, sortBy: "createdAt").Jql);
        Assert.EndsWith(" ORDER BY issuetype DESC, key ASC", Translate(null, sortBy: "issueType").Jql);
        Assert.EndsWith(" ORDER BY key DESC", Translate(null, sortBy: "jiraKey").Jql);
    }

    [Fact]
    public void The_status_category_is_a_sort_jql_can_express()
    {
        var t = Translate(null, sortBy: "statusCategory", sortDescending: false);
        Assert.EndsWith(" ORDER BY statusCategory ASC, key ASC", t.Jql);
        Assert.Empty(t.Warnings);
        Assert.EndsWith(" ORDER BY statusCategory DESC, key ASC", Translate(null, sortBy: "statusCategory").Jql);
    }

    [Fact]
    public void A_sort_jql_cannot_express_falls_back_to_key_and_says_so()
    {
        // Team has no custom-field mapping in an empty lookup, so there is nothing to ORDER BY.
        var t = Translate(null, sortBy: "team");
        Assert.EndsWith(" ORDER BY key ASC", t.Jql);
        Assert.Equal(new[] { "The JQL cannot sort by 'team', so it sorts by key." }, t.Warnings);
    }

    [Fact]
    public void A_mapped_team_or_story_points_sorts_through_its_custom_field()
    {
        var lookup = JqlLookup.Empty with { TeamField = "customfield_10001", StoryPointsField = "customfield_10016" };
        Assert.EndsWith(" ORDER BY cf[10001] DESC, key ASC", Translate(null, lookup, "team").Jql);
        Assert.EndsWith(" ORDER BY cf[10016] ASC, key ASC", Translate(null, lookup, "storyPoints", false).Jql);
        // A mapping that is not customfield_<digits> is not a field JQL can name this way.
        var odd = JqlLookup.Empty with { TeamField = "team" };
        Assert.Single(Translate(null, odd, "team").Warnings);
    }

    [Theory]
    [InlineData("status", "status")]
    [InlineData("issueType", "issuetype")]
    [InlineData("priority", "priority")]
    [InlineData("statusCategory", "statusCategory")]
    public void Dictionary_fields_compare_by_name(string field, string jql)
    {
        Assert.Equal($"{jql} = \"Done\"", Where(C(field, FilterOperator.Eq, "Done")));
        Assert.Equal($"{jql} != \"Done\"", Where(C(field, FilterOperator.Neq, "Done")));
        Assert.Equal($"{jql} in (\"To Do\", \"Done\")", Where(L(field, FilterOperator.In, "To Do", "Done")));
        Assert.Equal($"{jql} not in (\"To Do\", \"Done\")", Where(L(field, FilterOperator.NotIn, "To Do", "Done")));
        Assert.Equal($"{jql} is EMPTY", Where(C(field, FilterOperator.IsNull)));
        Assert.Equal($"{jql} is not EMPTY", Where(C(field, FilterOperator.IsNotNull)));
    }

    [Fact]
    public void Empty_in_a_list_follows_the_lists_empty_rule()
    {
        Assert.Equal("(status in (\"Done\") OR status is EMPTY)", Where(L("status", FilterOperator.In, "Done", null)));
        Assert.Equal("status is EMPTY", Where(L("status", FilterOperator.In, new object?[] { null })));
        // "is none of" never matches an empty value, here or in JQL: a null in it adds nothing.
        Assert.Equal("status not in (\"Done\")", Where(L("status", FilterOperator.NotIn, "Done", null)));
        Assert.Equal("status is not EMPTY", Where(L("status", FilterOperator.NotIn, new object?[] { null })));
    }

    [Fact]
    public void Strings_are_quoted_with_quotes_and_backslashes_escaped()
    {
        // The value is:  In "QA" \ 2   — the JQL literal:  "In \"QA\" \\ 2"
        Assert.Equal("status = \"In \\\"QA\\\" \\\\ 2\"", Where(C("status", FilterOperator.Eq, "In \"QA\" \\ 2")));
        Assert.StartsWith("project = \"A\\\"B\" ", Sut().Translate("A\"B", null, null, true, JqlLookup.Empty).Jql);
    }

    [Theory]
    [InlineData("a\nb", "\"a\\nb\"")]
    [InlineData("a\tb", "\"a\\tb\"")]
    [InlineData("a\rb", "\"a\\rb\"")]
    [InlineData("a\u0001b", "\"a\\u0001b\"")]
    [InlineData("a\u007Fb", "\"a\\u007fb\"")]
    [InlineData("a\u0085b", "\"a\\u0085b\"")]
    [InlineData("café 中", "\"café 中\"")]
    public void Control_characters_are_escaped_so_a_value_stays_on_one_line(string value, string literal)
    {
        Assert.Equal($"status = {literal}", Where(C("status", FilterOperator.Eq, value)));
        Assert.DoesNotContain('\n', Translate(All(C("status", FilterOperator.Eq, value))).Jql);
    }

    [Fact]
    public void Field_names_are_matched_the_way_the_compiler_matches_them()
    {
        Assert.Equal("status = \"Done\"", Where(C("Status", FilterOperator.Eq, "Done")));
        Assert.Equal("issuetype = \"Bug\"", Where(C("ISSUETYPE", FilterOperator.Eq, "Bug")));
        Assert.Equal("key = \"ACME-1\"", Where(C("JiraKey", FilterOperator.Eq, "ACME-1")));
        Assert.Equal("parent is EMPTY", Where(C("PARENT", FilterOperator.IsNull)));
        // Still the key rule, not "no field": the casing must not change which part is left out.
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("JIRAKEY", FilterOperator.Contains, "PLAT")));
        Assert.Contains("JQL cannot match part of an issue key", Assert.Single(t.Warnings));
    }

    [Fact]
    public void A_json_null_value_on_a_name_field_reads_as_empty()
    {
        var empty = J(null);
        Assert.Equal("status is EMPTY", Where(new FilterCondition("status", FilterOperator.Eq, empty, null)));
        // "is not (Empty)" is "has a value" — what the list's Neq of the empty value means.
        Assert.Equal("status is not EMPTY", Where(new FilterCondition("status", FilterOperator.Neq, empty, null)));
    }

    [Fact]
    public void Summary_text_has_the_searchs_reserved_characters_escaped()
    {
        // a:b (c++)  ->  a\:b \(c\+\+\)  ->  quoted, each backslash doubled.
        Assert.Equal("summary ~ \"a\\\\:b \\\\(c\\\\+\\\\+\\\\)\"", Where(C("summary", FilterOperator.Contains, "a:b (c++)")));
        // The trailing * of "starts with" is the wildcard, so it stays bare; one in the text does not.
        Assert.Equal("summary ~ \"c\\\\+\\\\+ \\\\*x*\"", Where(C("summary", FilterOperator.StartsWith, "C++ *x")));
        Assert.Equal("NOT summary ~ \"x\\\\/y\"", Where(C("summary", FilterOperator.NotContains, "x/y")));
    }

    [Fact]
    public void Summary_quotes_become_spaces_since_jira_would_read_them_as_a_phrase()
    {
        Assert.Equal("summary ~ \"say  hi \"", Where(C("summary", FilterOperator.Contains, "say \"hi\"")));
    }

    [Fact]
    public void Summary_text_is_lower_cased_so_and_or_not_are_words_not_operators()
    {
        Assert.Equal("summary ~ \"login and crash\"", Where(C("summary", FilterOperator.Contains, "Login AND Crash")));
        Assert.Equal("NOT summary ~ \"not or\"", Where(C("summary", FilterOperator.NotContains, "NOT OR")));
    }

    [Fact]
    public void Starts_with_is_trimmed_before_the_wildcard()
    {
        Assert.Equal("summary ~ \"crash*\"", Where(C("summary", FilterOperator.StartsWith, "  Crash  ")));
        Assert.Equal("summary ~ \"say*\"", Where(C("summary", FilterOperator.StartsWith, "say \"")));
    }

    [Theory]
    [InlineData(FilterOperator.Contains, "")]
    [InlineData(FilterOperator.Contains, "   ")]
    [InlineData(FilterOperator.StartsWith, "")]
    [InlineData(FilterOperator.NotContains, " \t")]
    [InlineData(FilterOperator.StartsWith, "\"\"")]
    public void Summary_search_with_no_text_is_left_out_not_a_bare_wildcard(FilterOperator op, string text)
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("summary", op, text)));
        Assert.Equal("status = \"Done\"", Where(t));
        var warning = Assert.Single(t.Warnings);
        Assert.Contains("(it has no text to search for)", warning);
        Assert.Contains("wider than this filter", warning);
    }

    [Fact]
    public void A_custom_field_id_takes_ascii_digits_only()
    {
        var odd = JqlLookup.Empty with { TeamField = "customfield_١٢" };   // Arabic-Indic digits
        Assert.Single(Translate(null, odd, "team").Warnings);
    }

    [Fact]
    public void Groups_join_with_and_or_and_nest_in_parentheses()
    {
        var filter = All(C("status", FilterOperator.Eq, "Done"))
            .With(Any(C("priority", FilterOperator.Eq, "High"), C("priority", FilterOperator.IsNull)));
        Assert.Equal("(status = \"Done\" AND (priority = \"High\" OR priority is EMPTY))", Where(Translate(filter)));
        Assert.Equal("(status = \"Done\" OR status = \"To Do\")",
            Where(Translate(Any(C("status", FilterOperator.Eq, "Done"), C("status", FilterOperator.Eq, "To Do")))));
    }

    [Fact]
    public void Disabled_parts_are_skipped_and_a_disabled_root_is_no_filter()
    {
        var filter = new FilterGroup(FilterLogic.And,
            new List<FilterCondition>
            {
                C("status", FilterOperator.Eq, "Done"),
                C("priority", FilterOperator.Eq, "Low") with { Disabled = true },
            },
            new List<FilterGroup> { Any(C("status", FilterOperator.IsNull)) with { Disabled = true } });
        Assert.Equal("status = \"Done\"", Where(Translate(filter)));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", Translate(filter with { Disabled = true }).Jql);
    }

    [Fact]
    public void Key_summary_and_parent()
    {
        Assert.Equal("key = \"ACME-1\"", Where(C("jiraKey", FilterOperator.Eq, "ACME-1")));
        Assert.Equal("key in (\"ACME-1\", \"ACME-2\")", Where(L("jiraKey", FilterOperator.In, "ACME-1", "ACME-2")));
        Assert.Equal("key not in (\"ACME-1\")", Where(L("jiraKey", FilterOperator.NotIn, "ACME-1")));
        Assert.Equal("parent = 10023", Where(C("parent", FilterOperator.Eq, "10023")));
        Assert.Equal("parent = \"x-1\"", Where(C("parent", FilterOperator.Eq, "x-1")));
        Assert.Equal("parent is EMPTY", Where(C("parent", FilterOperator.IsNull)));
        Assert.Equal("summary is not EMPTY", Where(C("summary", FilterOperator.IsNotNull)));
    }

    [Fact]
    public void Text_search_is_word_based_and_says_so_once()
    {
        var t = Translate(All(
            C("summary", FilterOperator.Contains, "login"),
            C("summary", FilterOperator.NotContains, "flaky"),
            C("summary", FilterOperator.StartsWith, "Crash")));
        Assert.Equal("(summary ~ \"login\" AND NOT summary ~ \"flaky\" AND summary ~ \"crash*\")", Where(t));
        Assert.Contains("word-based, not substring", Assert.Single(t.Warnings));
    }

    [Fact]
    public void A_left_out_part_of_an_all_group_makes_the_jql_wider()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"), C("jiraKey", FilterOperator.Contains, "PLAT")));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Equal(new[]
        {
            "Left out \"jiraKey Contains PLAT\" (JQL cannot match part of an issue key), so the JQL is wider than this filter: it can match issues this list does not show.",
        }, t.Warnings);
    }

    [Fact]
    public void A_left_out_part_of_an_any_group_makes_the_jql_narrower()
    {
        var t = Translate(Any(C("status", FilterOperator.Eq, "Done"), C("jiraKey", FilterOperator.StartsWith, "PLAT")));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("narrower than this filter", Assert.Single(t.Warnings));
    }

    [Fact]
    public void A_group_with_nothing_left_is_judged_by_the_group_holding_it()
    {
        // The Any group loses its only part, so the All above drops a requirement: wider.
        var t = Translate(All(C("status", FilterOperator.Eq, "Done")).With(Any(C("jiraKey", FilterOperator.Contains, "PLAT"))));
        Assert.Equal("status = \"Done\"", Where(t));
        Assert.Contains("wider than this filter", Assert.Single(t.Warnings));

        // Inside an Any, the same emptied group was an alternative: narrower.
        var u = Translate(Any(C("status", FilterOperator.Eq, "Done")).With(All(C("jiraKey", FilterOperator.Contains, "PLAT"))));
        Assert.Contains("narrower than this filter", Assert.Single(u.Warnings));
    }

    [Fact]
    public void An_any_group_inside_an_all_group_that_loses_a_part_but_keeps_one_is_narrower()
    {
        var t = Translate(All(C("status", FilterOperator.Eq, "Done"))
            .With(Any(C("priority", FilterOperator.Eq, "High"), C("jiraKey", FilterOperator.Contains, "PLAT"))));
        Assert.Equal("(status = \"Done\" AND priority = \"High\")", Where(t));
        Assert.Contains("narrower than this filter", Assert.Single(t.Warnings));
    }

    [Fact]
    public void Left_out_parts_of_an_emptied_group_inside_an_emptied_group_are_judged_two_levels_up()
    {
        var inner = Any(C("jiraKey", FilterOperator.StartsWith, "Q"));
        var middle = All(C("jiraKey", FilterOperator.Contains, "P")).With(inner);

        // Under an All root that keeps its own condition both parts were requirements: wider.
        var wide = Translate(All(C("status", FilterOperator.Eq, "Done")).With(middle));
        Assert.Equal("status = \"Done\"", Where(wide));
        Assert.Equal(2, wide.Warnings.Count);
        Assert.All(wide.Warnings, w => Assert.Contains("wider than this filter", w));

        // Under an Any root they were alternatives: narrower.
        var narrow = Translate(Any(C("status", FilterOperator.Eq, "Done")).With(middle));
        Assert.Equal("status = \"Done\"", Where(narrow));
        Assert.Equal(2, narrow.Warnings.Count);
        Assert.All(narrow.Warnings, w => Assert.Contains("narrower than this filter", w));
    }

    [Fact]
    public void When_nothing_is_left_the_jql_is_the_whole_project()
    {
        var t = Translate(Any(C("jiraKey", FilterOperator.Contains, "PLAT")));
        Assert.Equal("project = \"ACME\" ORDER BY updated DESC, key ASC", t.Jql);
        Assert.Contains("wider than this filter", Assert.Single(t.Warnings));
    }
}
