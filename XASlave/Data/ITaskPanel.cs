namespace XASlave.Data;

public interface ITaskPanel
{
    string Name { get; }
    string Label { get; }
    void Draw();
    void Initialize() { }
    void Dispose() { }
}
