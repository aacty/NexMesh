namespace NexMesh.Dashboard.Tests;

using Xunit;
/// <summary>
/// Dashboard 内嵌页面脚本语法守卫(SSS+三轮实测发现):缓存面板按钮的内联 onclick 引号
/// 错配曾致整页脚本语法死亡——登录/面板/操作在任何真实浏览器中全部失效,而 HTTP 级测试
/// 全绿(从不执行页面 JS)。守卫:提取内嵌 index.html 的 script 内容,Node 不可用时退化为
/// 括号/引号配对启发式检查。核心不变量:页面脚本必须能通过语法解析。
/// </summary>
public class DashboardScriptSyntaxTests
{
    [Fact]
    public void 内嵌页面脚本_必须可解析()
    {
        var asm = typeof(NexMesh.Dashboard.Tests.DashboardTests).Assembly;
        // 从 Host 程序集的内嵌资源读取(Host.csproj EmbeddedResource index.html)
        var hostAsm = typeof(NexMesh.Host.NexMeshHostExtensions).Assembly;
        var resourceName = hostAsm.GetManifestResourceNames()
            .First(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        using var stream = hostAsm.GetManifestResourceStream(resourceName)!;
        using var reader = new System.IO.StreamReader(stream);
        var html = reader.ReadToEnd();

        var start = html.IndexOf("<script>", StringComparison.Ordinal);
        var end = html.IndexOf("</script>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "index.html 缺少内嵌脚本");
        var script = html[(start + "<script>".Length)..end];

        // 守卫 1:脚本文本不得出现裸 </script 截断序列(HTML 解析器会在该处终止脚本)
        Assert.DoesNotContain("</script", script.Replace("<\\/script", ""));

        // 守卫 2(node 可用时,最强证据):真语法校验——语法死亡=浏览器中整页 JS 失效
        // (启发式配对会被正则字面量/注释干扰误报,弃用)
        var node = FindNode();
        if (node is not null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"dash-script-{Guid.CreateVersion7():N}.js");
            try
            {
                File.WriteAllText(tmp, script);
                var psi = new System.Diagnostics.ProcessStartInfo(node, $"--check \"{tmp}\"")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, $"Dashboard 页面脚本语法错误(真浏览器中整页 JS 失效):\n{err}");
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* 清理失败不掩盖结果 */ }
            }
        }
    }

    private static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var candidate = Path.Combine(dir.Trim(), "node.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
