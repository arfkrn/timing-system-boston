namespace boston_timing_system.Models
{
    public enum LaneStatus
    {
        Empty,
        Ready,
        Running,
        Finished,
        DNS, // Did Not Start
        DNF, // Did Not Finish
        DQ,  // Disqualified
        OFF  // Lane disabled/not in use
    }
}
