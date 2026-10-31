using UnityEngine;

namespace DiscoAutosave;

internal static class RecordSnapshot
{
    private const string Message = "OnRecordPersistentData";

    internal static void Record()
    {
        if (PixelCrushers.DialogueSystem.PersistentDataManager.recordPersistentDataOn.ToString() != "AllGameObjects")
        {
            PixelCrushers.DialogueSystem.PersistentDataManager.Record();
            return;
        }
        var targets = SaveSceneObjects.ReceiverSnapshot();
#if DISCO_DIAGNOSTICS
        SaveDiagnostics.Mark("record-receiver-discovery");
        SaveInternals.BeginTargetedRecord(targets.Length);
#endif
        try
        {
            foreach (var target in targets)
                if (target && target.activeInHierarchy) target.SendMessage(Message, SendMessageOptions.DontRequireReceiver);
        }
        finally
        {
#if DISCO_DIAGNOSTICS
            SaveInternals.EndTargetedRecord();
#endif
        }
    }
}
