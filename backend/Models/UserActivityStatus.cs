using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserActivityStatus
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Failure = 3
}
