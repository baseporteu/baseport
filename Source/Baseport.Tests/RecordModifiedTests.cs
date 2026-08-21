using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

// A bulk import writes raw SQL, so it never reaches RecordChangeInterceptor and the column keeps its default.
// Nothing that reads a record may hand that out as a modification date.
public class RecordModifiedTests
{
    private static Record Seeded(DateTime created, DateTime updated = default) =>
        new() { Id = Ids.NewShortId(12), TableId = "t", JsonData = "{}", CreatedAt = created, UpdatedAt = updated };

    [Fact]
    public void A_record_that_was_never_modified_reports_its_creation_time()
    {
        var created = new DateTime(2026, 5, 4, 10, 12, 28, DateTimeKind.Utc);

        Assert.Equal(created, Seeded(created).Modified);
    }

    [Fact]
    public void A_record_that_was_modified_reports_when()
    {
        var created = new DateTime(2026, 5, 4, 10, 12, 28, DateTimeKind.Utc);
        var edited = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(edited, Seeded(created, edited).Modified);
    }

    [Fact]
    public void The_api_never_serializes_the_column_default()
    {
        var created = new DateTime(2026, 5, 4, 10, 12, 28, DateTimeKind.Utc);

        var dto = ApiDtos.RecordDto(Seeded(created), []);

        Assert.DoesNotContain("0001-01-01", dto.ToJsonString());
        Assert.Equal(dto["createdAt"]!.GetValue<DateTime>(), dto["updatedAt"]!.GetValue<DateTime>());
    }
}
