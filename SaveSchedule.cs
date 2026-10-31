namespace DiscoAutosave;

public sealed class SaveSchedule
{
    private double elapsed;
    private string? destination;
    private double travelDeadline;
    private double? arrivedAt;
    private bool arrivalPending;

    public void Wait() => arrivedAt = null;

    public void CancelTravel()
    {
        destination = null;
        arrivedAt = null;
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
        arrivedAt = null;
        arrivalPending = false;
    }

    public bool Due(int minutes, string scene, double now, bool eligible)
    {
        if (destination != null)
        {
            if (now > travelDeadline)
            {
                destination = null;
                arrivedAt = null;
            }
            else if (eligible && scene == destination)
            {
                arrivedAt ??= now;
                if (now - arrivedAt.Value >= 2)
                {
                    arrivalPending = true;
                    destination = null;
                }
            }
            else arrivedAt = null;
        }
        return eligible && (arrivalPending || elapsed >= minutes * 60);
    }

    public void Reset()
    {
        elapsed = 0;
        CancelTravel();
    }
}
