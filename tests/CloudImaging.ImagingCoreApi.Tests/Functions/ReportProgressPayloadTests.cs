using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class ReportProgressPayloadTests
{
    [Fact]
    public async Task DeserializePayloadAsync_BindsClientCamelCasePayload()
    {
        const string json = """
            {
              "stepName": "DownloadImage",
              "status": "InProgress",
              "stepProgressPercent": 67,
              "errorDetail": null
            }
            """;
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var payload = await ReportProgressFunction.DeserializePayloadAsync(body);

        payload.Should().NotBeNull();
        payload!.StepName.Should().Be(ImagingStepName.DownloadImage);
        payload.Status.Should().Be(ImagingStepStatus.InProgress);
        payload.StepProgressPercent.Should().Be(67);
        payload.ErrorDetail.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"status\":\"InProgress\"}")]
    [InlineData("{\"stepName\":\"DownloadImage\"}")]
    public async Task DeserializePayloadAsync_RejectsMissingRequiredFields(string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = async () => await ReportProgressFunction.DeserializePayloadAsync(body);

        await action.Should().ThrowAsync<System.Text.Json.JsonException>();
    }
}