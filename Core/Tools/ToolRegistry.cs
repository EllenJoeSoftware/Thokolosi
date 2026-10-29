namespace Thokolosi.Core.Tools;

/// <summary>Thread-safe list of tools shared by all agents (chat, schedules, meetings). MCP tools are added as servers connect.</summary>
public class ToolRegistry
{
    readonly List<ITool> _tools = new();
    readonly object _gate = new();

    public void Add(ITool tool)
    {
        lock (_gate)
        {
            _tools.RemoveAll(t => t.Name == tool.Name);
            _tools.Add(tool);
        }
    }

    public IReadOnlyList<ITool> Snapshot()
    {
        lock (_gate) return _tools.ToList();
    }

    public ITool? Find(string name)
    {
        lock (_gate) return _tools.FirstOrDefault(t => t.Name == name);
    }
}
