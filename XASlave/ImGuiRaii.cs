using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace XASlave;

/// <summary>RAII coverage for ImGui scopes not exposed by Dalamud's <c>ImRaii</c>.</summary>
internal static class XASlaveImRaii
{
    public static WindowDisposable Window(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => new(name, flags);

    public static ClipRectDisposable ClipRect(Vector2 min, Vector2 max, bool intersectWithCurrentClipRect)
        => new(min, max, intersectWithCurrentClipRect);

    internal ref struct WindowDisposable
    {
        private bool disposed;

        public WindowDisposable(string name, ImGuiWindowFlags flags)
        {
            Success = ImGui.Begin(name, flags);
        }

        public bool Success { get; }

        public static implicit operator bool(WindowDisposable scope) => scope.Success;

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            ImGui.End();
        }
    }

    internal ref struct ClipRectDisposable
    {
        private bool disposed;

        public ClipRectDisposable(Vector2 min, Vector2 max, bool intersectWithCurrentClipRect)
        {
            ImGui.PushClipRect(min, max, intersectWithCurrentClipRect);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            ImGui.PopClipRect();
        }
    }
}
