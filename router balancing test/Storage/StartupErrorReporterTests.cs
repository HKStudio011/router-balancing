using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class StartupErrorReporterTests
{
    [Fact]
    public void Report_WritesExceptionDetailsToLogFile()
    {
        // Thư mục con cố ý chưa tồn tại — Report phải tự tạo để ghi được khi app data còn trống.
        var directory = Path.Combine(Path.GetTempPath(), $"startup-error-{Guid.NewGuid():N}");
        var logFilePath = Path.Combine(directory, "startup-error.log");
        var exception = new InvalidOperationException(
            "database migration failed",
            new InvalidOperationException("inner: table Providers is corrupt"));

        try
        {
            var result = StartupErrorReporter.Report(exception, logFilePath);

            Assert.Equal(logFilePath, result);
            var content = File.ReadAllText(logFilePath);
            Assert.Contains(typeof(InvalidOperationException).FullName!, content);
            Assert.Contains("database migration failed", content);
            Assert.Contains(exception.ToString(), content);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
