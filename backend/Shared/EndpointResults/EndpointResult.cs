using System.Reflection;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Shared;
using IResult = Microsoft.AspNetCore.Http.IResult;

namespace Shared.EndpointResults;

public sealed class EndpointResult : IResult, IEndpointMetadataProvider
{
    private readonly IResult _result;

    public EndpointResult(UnitResult<Error> result)
    {
        _result = result.IsSuccess
            ? new SuccessResult()
            : new ErrorResult(result.Error);
    }

    public Task ExecuteAsync(HttpContext httpContext) =>
        _result.ExecuteAsync(httpContext);

    public static implicit operator EndpointResult(UnitResult<Error> result) => new(result);

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(builder);

        // Success
        builder.Metadata.Add(new ProducesResponseTypeMetadata(200, typeof(Envelope), ["application/json"]));

        // Common errors
        builder.Metadata.Add(new ProducesResponseTypeMetadata(400, typeof(Envelope), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(500, typeof(Envelope), ["application/json"]));
    }

    public static EndpointResult ToEndpointResult(UnitResult<Error> result) => new(result);
}

public sealed class EndpointResult<TValue> : IResult, IEndpointMetadataProvider
{
    private readonly IResult _result;

    public EndpointResult(Result<TValue, Error> result)
    {
        _result = result.IsSuccess
            ? new SuccessResult<TValue>(result.Value)
            : new ErrorResult(result.Error);
    }

    public Task ExecuteAsync(HttpContext httpContext) =>
        _result.ExecuteAsync(httpContext);

    public static implicit operator EndpointResult<TValue>(Result<TValue, Error> result) => new(result);

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(builder);

        // Success
        builder.Metadata.Add(new ProducesResponseTypeMetadata(200, typeof(Envelope<TValue>), ["application/json"]));

        // Common errors
        builder.Metadata.Add(new ProducesResponseTypeMetadata(400, typeof(Envelope), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(500, typeof(Envelope), ["application/json"]));
    }

    public EndpointResult<TValue> ToEndpointResult(Result<TValue, Error> result) => new(result);
}