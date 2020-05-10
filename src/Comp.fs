module Infusion.Comp

open System
open System.Collections.Generic
open System.Text

open RimWorld
open Verse

open DefFields
open VerseTools
open Lib
open StatMod
open VerseInterop

type BestInfusionLabelLength =
    | Long
    | Short

// Holds an equipment's infusions.
[<AllowNullLiteral>]
type Infusion() =
    inherit ThingComp()

    static let mutable removalCandidates = Set.empty<Infusion>

    let mutable infusions = Set.empty<InfusionDef>
    let mutable removalSet = Set.empty<InfusionDef>

    let mutable quality = QualityCategory.Normal

    let mutable bestInfusionCache = None
    let mutable extraDamageCache = None

    let infusionsStatModCache = Dictionary<StatDef, option<StatMod>>()

    static member RemovalCandidates
        with get () = removalCandidates
        and set value = do removalCandidates <- value

    static member RegisterRemovalCandidate infusion =
        do Infusion.RemovalCandidates <- Set.add infusion Infusion.RemovalCandidates

    static member UnregisterRemovalCandidate infusion =
        do Infusion.RemovalCandidates <- Set.remove infusion Infusion.RemovalCandidates

    member this.Quality
        with get () = quality
        and set value = do quality <- value

    member this.Infusions
        with get () =
            infusions
            |> Seq.sortByDescending (fun inf -> inf.tier)
        and set (value: seq<InfusionDef>) =
            do this.InvalidateCache()
            do infusions <- value |> Set.ofSeq
            do removalSet <- Set.intersect removalSet infusions

    member this.RemovalSet
        with get () = removalSet
        and set value =
            do removalSet <- value
            if Set.isEmpty removalSet
            then Infusion.UnregisterRemovalCandidate this
            else Infusion.RegisterRemovalCandidate this

    member this.InfusionsRaw = infusions

    member this.InfusionsByPosition =
        let (prefixes, suffixes) =
            this.Infusions
            |> Seq.fold (fun (pre, suf) cur ->
                if cur.position = Position.Prefix then (cur :: pre, suf) else (pre, cur :: suf))
                   (List.empty, List.empty)

        (List.rev prefixes, List.rev suffixes)

    member this.BestInfusion =
        match bestInfusionCache with
        | None ->
            do bestInfusionCache <- this.Infusions |> Seq.tryHead
            bestInfusionCache
        | _ -> bestInfusionCache

    member this.ExtraDamages =
        if Option.isNone extraDamageCache then
            do extraDamageCache <-
                infusions
                |> Seq.map (fun def -> def.ExtraDamages)
                |> Seq.choose id
                |> Seq.concat
                |> Some

        Option.defaultValue Seq.empty extraDamageCache

    member this.Descriptions =
        this.Infusions
        |> Seq.map (fun inf -> inf.GetDescriptionString())
        |> String.concat "\n"

    member this.InspectionLabel =
        if Set.isEmpty infusions then
            ""
        else
            let (prefixes, suffixes) = this.InfusionsByPosition

            let suffixedPart =
                if List.isEmpty suffixes then
                    this.parent.def.label
                else
                    let suffixString =
                        (suffixes |> List.map (fun def -> def.label)).ToCommaList(true)

                    string (translate2 "Infusion.Label.Suffixed" suffixString this.parent.def.label)

            let prefixedPart =
                if List.isEmpty prefixes then
                    suffixedPart
                else
                    let prefixString =
                        prefixes
                        |> List.map (fun def -> def.label)
                        |> String.concat " "

                    string (translate2 "Infusion.Label.Prefixed" prefixString suffixedPart)

            prefixedPart.CapitalizeFirst()

    member this.Size = Set.count infusions

    member this.PopulateInfusionsStatModCache(stat: StatDef) =
        if not (infusionsStatModCache.ContainsKey stat) then
            let elligibles =
                infusions
                |> Seq.filter (fun inf -> inf.stats.ContainsKey stat)
                |> Seq.map (fun inf -> inf.stats.TryGetValue stat)

            let statMod =
                if Seq.isEmpty elligibles
                then None
                else elligibles |> Seq.fold (+) StatMod.empty |> Some

            do infusionsStatModCache.Add(stat, statMod)

    member this.GetModForStat(stat: StatDef) =
        do this.PopulateInfusionsStatModCache(stat)
        infusionsStatModCache.TryGetValue(stat, None)
        |> Option.defaultValue StatMod.empty

    member this.HasInfusionForStat(stat: StatDef) =
        do this.PopulateInfusionsStatModCache(stat)
        infusionsStatModCache.TryGetValue(stat, None)
        |> Option.isSome

    member this.InvalidateCache() =
        do bestInfusionCache <- None
        do infusionsStatModCache.Clear()

    member this.MarkForRemoval(infDef: InfusionDef) = do this.RemovalSet <- Set.add infDef removalSet

    member this.UnmarkForRemoval(infDef: InfusionDef) = do this.RemovalSet <- Set.remove infDef removalSet

    member this.MakeBestInfusionLabel length =
        match this.BestInfusion with
        | Some bestInf ->
            let sb =
                StringBuilder(if length = Long then bestInf.label else bestInf.LabelShort)

            if this.Size > 1 then
                do sb.Append("(+").Append(this.Size - 1).Append(")")
                   |> ignore

            string sb
        | None -> ""

    override this.TransformLabel label =
        match this.BestInfusion with
        | Some bestInf ->
            let parent = this.parent

            let baseLabel =
                GenLabel.ThingLabel(parent.def, parent.Stuff)

            let sb =
                match bestInf.position with
                | Position.Prefix -> translate2 "Infusion.Label.Prefixed" (this.MakeBestInfusionLabel Long) baseLabel
                | Position.Suffix -> translate2 "Infusion.Label.Suffixed" (this.MakeBestInfusionLabel Long) baseLabel
                | _ -> raise (ArgumentException("Position must be either Prefix or Suffix"))
                |> string
                |> StringBuilder

            // components
            // quality should never be None but let's be cautious
            let quality =
                compOfThing<CompQuality> parent
                |> Option.map (fun cq -> cq.Quality.GetLabel())

            let hitPoints =
                if parent.def.useHitPoints
                   && parent.HitPoints < parent.MaxHitPoints
                   && parent.def.stackLimit = 1 then
                    Some
                        ((float32 parent.HitPoints
                          / float32 parent.MaxHitPoints).ToStringPercent())
                else
                    None

            let tainted =
                match parent with
                | :? Apparel as apparel -> if apparel.WornByCorpse then Some(translate "WornByCorpseChar") else None
                | _ -> None

            do [ quality; hitPoints; tainted ]
               |> List.choose id
               |> String.concat " "
               |> (fun str ->
                   if not (str.NullOrEmpty())
                   then sb.Append(" (").Append(str).Append(")") |> ignore)

            string sb
        | None -> label

    override this.PostSpawnSetup(respawningAfterLoad) =
        if not (respawningAfterLoad || Seq.isEmpty removalSet)
        then do Infusion.RegisterRemovalCandidate this

    override this.PostDeSpawn(_) = do Infusion.UnregisterRemovalCandidate this

    override this.GetDescriptionPart() = this.Descriptions

    override this.DrawGUIOverlay() =
        if Find.CameraDriver.CurrentZoom
           <= CameraZoomRange.Close then
            match this.BestInfusion with
            | Some bestInf ->
                do GenMapUI.DrawThingLabel
                    (GenMapUI.LabelDrawPosFor(this.parent, -0.6499999762f),
                     this.MakeBestInfusionLabel Short,
                     tierToColor bestInf.tier)
            | None -> ()

    override this.PostExposeData() =
        let mutable savedQuality = QualityCategory.Normal
        if (Scribe.mode = LoadSaveMode.LoadingVars) then
            do Scribe_Values.Look(&savedQuality, "quality")

            do this.Quality <- savedQuality

        let savedInfusions =
            scribeDefCollection "infusion" infusions
            |> Option.defaultValue Seq.empty

        let savedRemovals =
            scribeDefCollection "removal" removalSet
            |> Option.map Set.ofSeq
            |> Option.defaultValue Set.empty

        if (Scribe.mode = LoadSaveMode.LoadingVars) then
            do this.Infusions <- savedInfusions
            // registering / unregistering is covered by PostSpawnSetup / PostDeSpawn
            do removalSet <- savedRemovals

    override this.AllowStackWith(_) = false

    override this.GetHashCode() = this.parent.thingIDNumber

    override this.Equals(ob: obj) =
        match ob with
        | :? Infusion as comp -> this.parent.thingIDNumber = comp.parent.thingIDNumber
        | _ -> false

    interface IComparable with
        member this.CompareTo(ob: obj) =
            match ob with
            | :? Infusion as comp ->
                let thingID = comp.parent.ThingID
                this.parent.ThingID.CompareTo thingID
            | _ -> 0

let addInfusion (infDef: InfusionDef) (comp: Infusion) =
    do comp.Infusions <-
        seq {
            yield infDef
            yield! comp.Infusions
        }

let setInfusions (infDefs: seq<InfusionDef>) (comp: Infusion) = do comp.Infusions <- infDefs

/// Picks elligible `InfusionDef` for the `Thing`.
let pickInfusions (quality: QualityCategory) (parent: ThingWithComps) =
    // requirement fields
    let checkAllowance (infDef: InfusionDef) =
        if parent.def.IsApparel then infDef.requirements.allowance.apparel
        elif parent.def.IsMeleeWeapon then infDef.requirements.allowance.melee
        elif parent.def.IsRangedWeapon then infDef.requirements.allowance.ranged
        else false

    let checkDisabled (infDef: InfusionDef) = not infDef.disabled

    let checkTechLevel (infDef: InfusionDef) =
        infDef.requirements.techLevel
        |> Seq.contains parent.def.techLevel

    let checkQuality (infDef: InfusionDef) = (infDef.ChanceFor quality) > 0.0f

    let checkDamageType (infDef: InfusionDef) =
        if parent.def.IsApparel
           || infDef.requirements.meleeDamageType = DamageType.Anything then
            true
        else
            parent.def.tools
            |> Seq.reduce (fun a b -> if a.power > b.power then a else b)
            |> isToolCapableOfDamageType infDef.requirements.meleeDamageType

    // chance
    let checkChance (infDef: InfusionDef) =
        let chance =
            infDef.ChanceFor(quality)
            * Settings.getChanceFactor ()

        Rand.Chance chance

    // slots
    let slotBonuses =
        Option.ofObj parent.def.apparel
        // one more per 4 body part groups, one more per layers
        |> Option.map (fun s ->
            (max 0 (s.bodyPartGroups.Count - 4))
            + s.layers.Count
            - 1)
        |> Option.defaultValue 0

    DefDatabase<InfusionDef>.AllDefs
    |> Seq.filter
        (checkDisabled
         <&> checkAllowance
         <&> checkTechLevel
         <&> checkQuality
         <&> checkDamageType)
    |> Seq.map (fun infDef ->
        (infDef,
         (infDef.WeightFor quality)
         * (Settings.getWeightFactor ())
         + Rand.Value)) // weighted, duh
    |> Seq.sortByDescending snd
    |> Seq.truncate (Settings.getBaseSlotsFor quality + slotBonuses)
    |> Seq.map fst
    |> Seq.filter checkChance
    |> List.ofSeq // need to "finalize" the random sort
    |> List.sortBy (fun infDef -> infDef.tier)

let removeMarkedInfusions (comp: Infusion) =
    do comp.Infusions <- Set.difference comp.InfusionsRaw comp.RemovalSet
    do comp.RemovalSet <- Set.empty

let removeAllInfusions (comp: Infusion) = do comp.Infusions <- Set.empty

let removeInfusion (infDef: InfusionDef) (comp: Infusion) =
    do comp.Infusions <- Set.remove infDef comp.InfusionsRaw
