using System.Collections.Generic;
using System.Linq;

namespace Limber.Rig;

// All of Limber's goals on one character.
public sealed class ActorRig
{
    public nint Address { get; }
    public ushort ObjectIndex { get; }
    public string Name { get; set; }

    public List<Goal> Goals { get; } = [];

    public ActorRig(nint address, ushort objectIndex, string name)
    {
        Address = address;
        ObjectIndex = objectIndex;
        Name = name;
    }

    public Goal? Find(string bone) => Goals.FirstOrDefault(g => g.Bone == bone);

    public Goal GetOrAdd(string bone)
    {
        var goal = Find(bone);
        if(goal != null)
            return goal;

        goal = HandleCatalog.CreateGoal(bone);
        Goals.Add(goal);
        return goal;
    }

    public bool Remove(string bone) => Goals.RemoveAll(g => g.Bone == bone) > 0;

    public bool HasAttachments => Goals.Any(g => g.Space == GoalSpace.Attached && g.Enabled);
}
