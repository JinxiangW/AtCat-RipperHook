using System;
using AssetRipper.SourceGenerated.Classes.ClassID_78;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.Endfield;

public class EndFieldCommon_Hook : RipperHookCommon
{
	[RetargetMethod(typeof(TagManagerExtensions), "TagIDToName", new Type[] { })]
	public static string TagIDToName(ITagManager? tagManager, int tagID)
	{
		switch (tagID)
		{
		case 0:
			return "Untagged";
		case 1:
			return "Respawn";
		case 2:
			return "Finish";
		case 3:
			return "EditorOnly";
		case 5:
			return "MainCamera";
		case 6:
			return "Player";
		case 7:
			return "GameController";
		default:
			if (tagManager != null)
			{
				int num = tagID - 20000;
				if (num < tagManager.Tags.Count)
				{
					if (num >= 0)
					{
						return tagManager.Tags[num].String;
					}
					if (!tagManager.IsBrokenCustomTags())
					{
						return $"unknown_{tagID}";
					}
				}
			}
			return $"unknown_{tagID}";
		}
	}
}
