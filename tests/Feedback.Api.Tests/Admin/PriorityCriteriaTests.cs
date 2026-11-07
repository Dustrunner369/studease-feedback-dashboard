using System.Net;
using System.Net.Http.Json;
using Feedback.Api.Tests.Infrastructure;

namespace Feedback.Api.Tests.Admin;

public sealed class PriorityCriteriaTests(FeedbackDatabase database) : AdminTest(database)
{
    [Fact]
    public async Task Settings_shows_the_criteria_from_the_config_file()
    {
        var criteria = (await Client.GetFromJsonAsync<CriteriaJson>("/api/admin/criteria"))!;

        Assert.Equal(
            ["Help students find new study spots", "Make students come back", "Grow the social side"],
            criteria.Goals);
        Assert.Equal(
            [("Strategic fit", 30), ("Reach", 30), ("Pain", 25), ("Retention risk", 15)],
            criteria.Criteria.Select(c => (c.Name, c.Weight)));

        var pain = criteria.Criteria.Single(c => c.Name == "Pain");
        Assert.Equal(5, pain.Levels!.Length);
        Assert.Equal("Nice to have; the app works fine without it", pain.Levels[0]);
        Assert.Equal("Says they've stopped using or uninstalled Studease",
            criteria.Criteria.Single(c => c.Name == "Retention risk").Levels![4]);
        Assert.Equal("Directly advances one goal", criteria.Criteria.Single(c => c.Name == "Strategic fit").Levels![3]);
        Assert.Null(criteria.Criteria.Single(c => c.Name == "Reach").Levels);

        Assert.Equal([(0, 1), (3, 2), (10, 3), (20, 4), (40, 5)], criteria.ReachBands.Select(b => (b.MinPoints, b.Rating)));
        Assert.Equal(70, criteria.CutOffs.High);
        Assert.Equal(40, criteria.CutOffs.Medium);
    }

    [Fact]
    public async Task The_criteria_need_the_admin_cookie()
    {
        await Client.PostAsync("/api/admin/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/admin/criteria")).StatusCode);
    }
}

public sealed record CriteriaJson(string[] Goals, CriterionJson[] Criteria, ReachBandJson[] ReachBands, CutOffsJson CutOffs);

public sealed record CriterionJson(string Name, int Weight, string RatedBy, string[]? Levels);

public sealed record ReachBandJson(int MinPoints, int Rating);

public sealed record CutOffsJson(int High, int Medium);
