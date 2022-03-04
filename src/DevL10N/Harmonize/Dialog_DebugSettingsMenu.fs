module DevL10N.Harmonize.Dialog_DebugSettingsMenu

open System.Reflection
open System.Reflection.Emit

open HarmonyLib
open Verse

open DevL10N.Lib


[<HarmonyPatch(typeof<Dialog_DebugSettingsMenu>, "DoField")>]
module DoField =
  // HOW LAZY AM I
  // [todo] Use compiler directives
  let __Prefix (fi: FieldInfo) =
    let name =
      GenText
        .SplitCamelCase(fi.Name)
        .ToLower()
        .CapitalizeFirst()

    Log.Message(taggify "" (fi.Name.CapitalizeFirst()) (name.TranslateSimple().Split('[') |> Seq.head))
    true

  let Transpiler (instructions: CodeInstruction seq) =
    let insts = List.ofSeq instructions

    let first, others =
      insts
      |> List.findIndex (fun inst ->
        inst.opcode = OpCodes.Call
        && (inst.operand :?> MethodInfo) = AccessTools.Method(typeof<GenText>, "SplitCamelCase"))
      |> splitFlipped insts

    // just removing SplitCamelCase() call...
    let capitalize, rest = others |> List.tail |> headTail

    seq {
      yield! first
      yield capitalize
      yield CodeInstruction(OpCodes.Call, AccessTools.Method(typeof<Translator>, "TranslateSimple"))
      yield! rest
    }
