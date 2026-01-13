using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Shared;

public class InjectJSONSerializeConfig : IConfigureOptions<JsonOptions>
{
    public void Configure(JsonOptions options) => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
}