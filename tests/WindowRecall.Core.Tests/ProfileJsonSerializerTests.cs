using System.Text.Json.Nodes;
using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class ProfileJsonSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesSchemaAndUsesReadableEnums()
    {
        LayoutProfile expected = ProfileFixture.Create(persistTitles: true);
        string json = ProfileJsonSerializer.Serialize(expected);
        LayoutProfile actual = ProfileJsonSerializer.Deserialize(json);

        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.CapturedAtUtc, actual.CapturedAtUtc);
        Assert.Equal(expected.Displays[0], actual.Displays[0]);
        Assert.Equal(expected.Windows[0], actual.Windows[0]);
        Assert.Equal(expected.Privacy, actual.Privacy);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"normal\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPrivacy_DoesNotPersistRawTitle()
    {
        string json = ProfileJsonSerializer.Serialize(ProfileFixture.Create());

        Assert.DoesNotContain("Private document", json, StringComparison.Ordinal);
        Assert.Null(ProfileJsonSerializer.Deserialize(json).Windows[0].Title);
    }

    [Fact]
    public void ExplicitPrivacyOptIn_PersistsRawTitle() =>
        Assert.Contains("Private document", ProfileJsonSerializer.Serialize(ProfileFixture.Create(persistTitles: true)), StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"one\"}")]
    public void MalformedInput_ThrowsClearFormatException(string json) =>
        Assert.Throws<ProfileFormatException>(() => ProfileJsonSerializer.Deserialize(json));

    [Fact]
    public void FutureVersion_IsRejectedClearly()
    {
        UnsupportedProfileVersionException error = Assert.Throws<UnsupportedProfileVersionException>(
            () => ProfileJsonSerializer.Deserialize("{\"schemaVersion\": 99}"));

        Assert.Equal(99, error.Version);
    }

    [Fact]
    public void InvalidSchema_IsRejected()
    {
        LayoutProfile invalid = ProfileFixture.Create() with { Name = " " };
        ProfileFormatException error = Assert.Throws<ProfileFormatException>(() => ProfileJsonSerializer.Serialize(invalid));
        Assert.Contains("name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingRequiredRootProperty_IsRejected()
    {
        JsonObject profile = JsonNode.Parse(ProfileJsonSerializer.Serialize(ProfileFixture.Create()))!.AsObject();
        profile.Remove("capturedAtUtc");

        Assert.Throws<ProfileFormatException>(() => ProfileJsonSerializer.Deserialize(profile.ToJsonString()));
    }

    [Fact]
    public void MissingRequiredNestedProperty_IsRejected()
    {
        JsonObject profile = JsonNode.Parse(ProfileJsonSerializer.Serialize(ProfileFixture.Create()))!.AsObject();
        profile["windows"]!.AsArray()[0]!.AsObject().Remove("state");

        Assert.Throws<ProfileFormatException>(() => ProfileJsonSerializer.Deserialize(profile.ToJsonString()));
    }
}
