using Wizards.Arena.Promises;

namespace ForgeBridge
{
    /// <summary>A promise completed later by SetResult. MTGA build 26149138 removed SimplePromise's empty constructor and SetResult
    /// (SimplePromise is sealed), so this derives from Promise itself (Start and Complete are protected there).</summary>
    public sealed class OpenPromise<T> : Promise<T>
    {
        public OpenPromise() { Start(); }
        public void SetResult(T result) { Complete(result); }
    }
}
