using Egoist.Voice.Services;

namespace Egoist.Voice.Tests;

public sealed class AppLogPrivacyTests
{
    [Fact]
    public async Task Sensitive_scope_is_nestable_and_flows_to_child_async_work()
    {
        Assert.False(AppLog.IsSensitiveDataSuppressed);

        using (AppLog.SuppressSensitiveData())
        {
            Assert.True(AppLog.IsSensitiveDataSuppressed);
            Assert.True(await Task.Run(() => AppLog.IsSensitiveDataSuppressed));

            using (AppLog.SuppressSensitiveData())
            {
                Assert.True(AppLog.IsSensitiveDataSuppressed);
            }

            Assert.True(AppLog.IsSensitiveDataSuppressed);
        }

        Assert.False(AppLog.IsSensitiveDataSuppressed);
    }
}
