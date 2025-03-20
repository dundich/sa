namespace Sa.StateMachine;

public static class SmStateId
{
    // sys ids
    public const int Start = 1;
    public const int Finish = 0;
    public const int Error = -1;
    // user ids
    public const int WaitingToRun = 101;
    public const int Running = 102;
    public const int Succeed = 200;
}
