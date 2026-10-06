namespace MultiDisplayVCPServer.Messaging
{
    /// <summary>
    /// Published via MessagePipe whenever the server's running state changes.
    /// Replaces the static ServerStateChanged event in Program.cs.
    /// </summary>
    public sealed class ServerStateMessage
    {
        /// <summary>0 = Stopped, 1 = Running, 2 = Busy/Restarting</summary>
        public int State { get; init; }
    }
}
