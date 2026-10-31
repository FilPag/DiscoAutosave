namespace DiscoAutosave;

public sealed class SaveSchedule
{
    private double elapsed;
    private string? destination;
    private double travelDeadline;
    private bool arrivalPending;

    public void CancelTravel()
    {
        destination = null;
        arrivalPending = false;
    }

    public void Advance(double seconds, bool active)
    {
        if (active) elapsed += Math.Clamp(seconds, 0, 1);
    }

    public void Travel(string scene, double now)
    {
        destination = scene;
        travelDeadline = now + 120;
        arrivalPending = false;
    }

    public bool Due(int minutes, string scene, double now, bool eligible)
    {
        if (destination != null)
        {
            if (now > travelDeadline)
            {
                destination = null;
            }
            else if (eligible && scene == destination)
            {
                arrivalPending = true;
                destination = null;
            }
        }
        return eligible && (arrivalPending || elapsed >= minutes * 60);
    }

    public void Reset()
    {
        elapsed = 0;
        CancelTravel();
    }
}
