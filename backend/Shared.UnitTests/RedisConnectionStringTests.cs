using Shared.Redis;

namespace Shared.UnitTests;

public class RedisConnectionStringTests
{
    [Fact]
    public void A_plain_connection_string_gets_fail_fast_settings_added()
    {
        var result = RedisConnectionString.Resilient("redis:6379,password=secret");

        Assert.StartsWith("redis:6379,password=secret,", result);
        Assert.Contains("abortConnect=false", result);
        Assert.Contains("connectTimeout=500", result);
        Assert.Contains("syncTimeout=500", result);
        Assert.Contains("asyncTimeout=500", result);
    }

    [Fact]
    public void What_the_connection_string_already_says_is_left_alone()
    {
        var result = RedisConnectionString.Resilient("redis:6379,abortConnect=True,ASYNCTIMEOUT=2000");

        Assert.Contains("abortConnect=True", result);
        Assert.DoesNotContain("abortConnect=false", result);
        Assert.Contains("ASYNCTIMEOUT=2000", result);
        Assert.DoesNotContain("asyncTimeout=500", result);
        Assert.Contains("connectTimeout=500", result);
    }

    [Fact]
    public void A_sentinel_connection_string_keeps_its_endpoints_and_service_name()
    {
        var result = RedisConnectionString.Resilient("s1:26379,s2:26379,s3:26379,serviceName=mymaster,password=p");

        Assert.StartsWith("s1:26379,s2:26379,s3:26379,serviceName=mymaster,password=p,", result);
    }

    [Fact]
    public void An_empty_connection_string_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RedisConnectionString.Resilient(" "));
    }
}
