namespace TurboPump
{
    /// <summary>
    /// An asynchronous operation will be executed by a thread.
    /// </summary>
    public interface IRunnable
    {
        /// <summary>
        /// Executes the action.
        /// </summary>
        void Run();
    }
}