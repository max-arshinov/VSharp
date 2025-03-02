namespace VSharp.Core

open System
open System.Text
open VSharp
open VSharp.CSharpUtils
open TypeUtils
open VSharp.Core


type IMemoryKey<'a, 'reg when 'reg :> IRegion<'reg>> =
//    abstract IsAllocated : bool
    abstract Region : 'reg
    abstract Map : (term -> term) -> (Type -> Type) -> (vectorTime -> vectorTime) -> 'reg -> 'reg * 'a
    abstract IsUnion : bool
    abstract Unguard : (term * 'a) list

type regionSort =
    | HeapFieldSort of fieldId
    | StaticFieldSort of fieldId
    | ArrayIndexSort of arrayType
    | ArrayLengthSort of arrayType
    | ArrayLowerBoundSort of arrayType
    | StackBufferSort of stackKey
    member x.TypeOfLocation =
        match x with
        | HeapFieldSort field
        | StaticFieldSort field -> field.typ
        | ArrayIndexSort(elementType, _, _) -> elementType
        | ArrayLengthSort _
        | ArrayLowerBoundSort _ -> typeof<int32>
        | StackBufferSort _ -> typeof<int8>

module private MemoryKeyUtils =

    let regionOfHeapAddress = function
        | {term = ConcreteHeapAddress addr} -> intervals<vectorTime>.Singleton addr
        | addr -> intervals<vectorTime>.Closed VectorTime.minfty (timeOf addr)

    let regionOfIntegerTerm = function
        | {term = Concrete(:? int as value, typ)} when typ = lengthType -> points<int>.Singleton value
        | _ -> points<int>.Universe

    let regionsOfIntegerTerms = List.map regionOfIntegerTerm >> listProductRegion<points<int>>.OfSeq

[<StructuralEquality;CustomComparison>]
type heapAddressKey =
    {address : heapAddress}
    interface IMemoryKey<heapAddressKey, vectorTime intervals> with
//        override x.IsAllocated =
//            match x.address.term with
//            | ConcreteHeapAddress _ -> true
//            | _ -> false
        override x.Region = MemoryKeyUtils.regionOfHeapAddress x.address
        override x.Map mapTerm _ mapTime reg =
            let symbolicReg = intervals<vectorTime>.Closed VectorTime.minfty VectorTime.zero
            let newReg =
                if (reg :> IRegion<vectorTime intervals>).CompareTo symbolicReg = Includes then
                    let rightBound = mapTime []
                    assert(not <| VectorTime.isEmpty rightBound)
                    let reg' = (reg :> IRegion<vectorTime intervals>).Subtract symbolicReg
                    let mappedZeroInterval = intervals<vectorTime>.Closed VectorTime.minfty rightBound
                    mappedZeroInterval.Union(reg'.Map mapTime)
                else
                    reg.Map mapTime
            newReg, {address = mapTerm x.address}
        override x.IsUnion = isUnion x.address
        override x.Unguard = Merging.unguard x.address |> List.map (fun (g, addr) -> (g, {address = addr}))
    interface IComparable with
        override x.CompareTo y =
            match y with
            | :? heapAddressKey as y -> compareTerms x.address y.address
            | _ -> -1
    override x.ToString() = x.address.ToString()

[<StructuralEquality;CustomComparison>]
type heapArrayIndexKey =
    {address : heapAddress; indices : term list}
    interface IMemoryKey<heapArrayIndexKey, productRegion<vectorTime intervals, int points listProductRegion>> with
//        override x.IsAllocated =
//            match x.address.term with
//            | ConcreteHeapAddress _ -> true
//            | _ -> false
        override x.Region =
            productRegion<vectorTime intervals, int points listProductRegion>.ProductOf (MemoryKeyUtils.regionOfHeapAddress x.address) (MemoryKeyUtils.regionsOfIntegerTerms x.indices)
        override x.Map mapTerm _ mapTime reg =
            reg.Map (fun x -> x.Map mapTime) id, {address = mapTerm x.address; indices = List.map mapTerm x.indices}
        override x.IsUnion = isUnion x.address
        override x.Unguard = Merging.unguard x.address |> List.map (fun (g, addr) -> (g, {address = addr; indices = x.indices}))  // TODO: if x.indices is the union of concrete values, then unguard indices as well
    interface IComparable with
        override x.CompareTo y =
            match y with
            | :? heapArrayIndexKey as y ->
                let cmp = compareTerms x.address y.address
                if cmp = 0 then List.compareWith compareTerms x.indices y.indices
                else cmp
            | _ -> -1
    override x.ToString() = sprintf "%O[%O]" x.address (x.indices |> List.map toString |> join ", ")

[<StructuralEquality;CustomComparison>]
type heapVectorIndexKey =
    {address : heapAddress; index : term}
    interface IMemoryKey<heapVectorIndexKey, productRegion<vectorTime intervals, int points>> with
//        override x.IsAllocated =
//            match x.address.term with
//            | ConcreteHeapAddress _ -> true
//            | _ -> false
        override x.Region =
            productRegion<vectorTime intervals, int points>.ProductOf (MemoryKeyUtils.regionOfHeapAddress x.address) (MemoryKeyUtils.regionOfIntegerTerm x.index)
        override x.Map mapTerm _ mapTime reg =
            reg.Map (fun x -> x.Map mapTime) id, {address = mapTerm x.address; index = mapTerm x.index}
        override x.IsUnion = isUnion x.address
        override x.Unguard = Merging.unguard x.address |> List.map (fun (g, addr) -> (g, {address = addr; index = x.index}))  // TODO: if x.index is the union of concrete values, then unguard index as well
    interface IComparable with
        override x.CompareTo y =
            match y with
            | :? heapVectorIndexKey as y ->
                let cmp = compareTerms x.address y.address
                if cmp = 0 then compareTerms x.index y.index
                else cmp
            | _ -> -1
    override x.ToString() = sprintf "%O[%O]" x.address x.index

[<StructuralEquality;CustomComparison>]
type stackBufferIndexKey =
    {index : term}
    interface IMemoryKey<stackBufferIndexKey, int points> with
//        override x.IsAllocated = true
        override x.Region = MemoryKeyUtils.regionOfIntegerTerm x.index
        override x.Map mapTerm _ _ reg =
            reg, {index = mapTerm x.index}
        override x.IsUnion =
            match x.index.term with
            | Union gvs when List.forall (fst >> isConcrete) gvs -> true
            | _ -> false
        override x.Unguard =
            match x.index.term with
            | Union gvs when List.forall (fst >> isConcrete) gvs -> gvs |> List.map (fun (g, idx) -> (g, {index = idx}))
            | _ -> [(True, x)]
    interface IComparable with
        override x.CompareTo y =
            match y with
            | :? stackBufferIndexKey as y -> compareTerms x.index y.index
            | _ -> -1
    override x.ToString() = x.index.ToString()

[<CustomEquality;CustomComparison>]
type typeWrapper = {t : Type}
with
    interface IAtomicRegion<typeWrapper> with
        override x.Intersect y = structuralInfimum x.t y.t |> Option.map (fun t -> {t = t})
    override x.GetHashCode() = x.t.GetDeterministicHashCode()
    override x.Equals(o : obj) =
        match o with
        | :? typeWrapper as y -> x.t.TypeHandle = y.t.TypeHandle
        | _ -> false
    interface IComparable with
        override x.CompareTo(other) =
            match other with
            | :? typeWrapper as y -> compare x.t.TypeHandle.Value y.t.TypeHandle.Value
            | _ -> -1

[<CustomEquality;CustomComparison>]
type symbolicTypeKey =
    {typ : Type}
    interface IMemoryKey<symbolicTypeKey, freeRegion<typeWrapper>> with
//        override x.IsAllocated = false // TODO: when statics are allocated? always or never? depends on our exploration strategy
        override x.Region = freeRegion<typeWrapper>.Singleton {t = x.typ}
        override x.Map _ mapper _ reg =
            reg.Map (fun t -> {t = mapper t.t}), {typ = mapper x.typ}
        override x.IsUnion = false
        override x.Unguard = [(True, x)]
    override x.ToString() = x.typ.ToString()
    override x.GetHashCode() = x.typ.GetDeterministicHashCode()
    override x.Equals(o : obj) =
        match o with
        | :? symbolicTypeKey as y -> x.typ.TypeHandle.Value = y.typ.TypeHandle.Value
        | _ -> false
    interface IComparable with
        override x.CompareTo(other) =
            match other with
            | :? symbolicTypeKey as y -> compare x.typ.TypeHandle.Value y.typ.TypeHandle.Value
            | _ -> -1

type updateTreeKey<'key, 'value when 'key : equality> =
    {key : 'key; value : 'value}
    interface IRegionTreeKey<updateTreeKey<'key, 'value>> with
        override x.Hides y = x.key = y.key
    override x.ToString() = (x.key, x.value).ToString()
type updateTree<'key, 'value, 'reg when 'key :> IMemoryKey<'key, 'reg> and 'reg :> IRegion<'reg> and 'key : equality and 'value : equality and 'reg : equality> =
    regionTree<updateTreeKey<'key, 'value>, 'reg>

module private UpdateTree =
    let empty<'key, 'value, 'reg when 'key :> IMemoryKey<'key, 'reg> and 'reg :> IRegion<'reg> and 'key : equality and 'value : equality and 'reg : equality> =
        RegionTree.empty<updateTreeKey<'key, 'value>, 'reg>

    let isEmpty tree = RegionTree.isEmpty tree

    let read (key : 'key) isDefault makeSymbolic makeDefault (tree : updateTree<'key, 'value, 'reg>) =
        let reg = key.Region
        let d = RegionTree.localize reg tree
        if PersistentDict.isEmpty d then
            if isDefault key then makeDefault() else makeSymbolic (Node d)
        elif PersistentDict.size d = 1 then
            match PersistentDict.tryFind d reg with
            | Some({key=key'; value=v}, _) when key = key' -> v
            | Some _ -> makeSymbolic (Node d)
            | _ -> makeSymbolic (Node d)
        else makeSymbolic (Node d)

    let memset (keyAndValues : seq<'key * 'value>) (tree : updateTree<'key, 'value, 'reg>) =
        let keyAndRegions = keyAndValues |> Seq.map (fun (key, value) -> key.Region, {key=key; value=value})
        RegionTree.memset keyAndRegions tree

    let write (key : 'key) (value : 'value) (tree : updateTree<'key, 'value, 'reg>) =
        RegionTree.write key.Region {key=key; value=value} tree

    let map (mapKey : 'reg -> 'key -> 'reg * 'key) mapValue (tree : updateTree<'key, 'value, 'reg>) =
        RegionTree.map (fun reg {key=k; value=v} -> let reg', k' = mapKey reg k in (reg', k'.Region, {key=k'; value=mapValue v})) tree

    let compose (earlier : updateTree<'key, 'value, 'reg>) (later : updateTree<'key, 'value, 'reg>) =
        later |> RegionTree.foldr (fun reg key trees ->
            list {
                let! g, tree = trees
                let! g', k = key.key.Unguard
                let key' = {key with key = k}
                return (g &&& g', RegionTree.write reg key' tree)
            }) [(True, earlier)]

    let deterministicCompose earlier later = RegionTree.append earlier later

    let print indent valuePrint tree =
        let window = 50
        let incIndent = indent + "    "
        let lines = tree |> RegionTree.flatten |> List.map (fun (_, {key=k; value=v}) -> sprintf "%O <- %O" k (valuePrint v))
        if lines |> List.sumBy (fun s -> s.Length) > window then
            sprintf "{\n%s\n%s}" (lines |> List.map (fun s -> incIndent + s) |> join "\n") indent
        else sprintf "{%s}" (join "; " lines)

    let localize reg tree = RegionTree.localize reg tree |> Node

    let forall predicate tree = RegionTree.foldl (fun acc _ k -> acc && predicate k) true tree

[<StructuralEquality; NoComparison>]
type memoryRegion<'key, 'reg when 'key : equality and 'key :> IMemoryKey<'key, 'reg> and 'reg : equality and 'reg :> IRegion<'reg>> =
    {typ : Type; updates : updateTree<'key, term, 'reg>; defaultValue : term option}

module MemoryRegion =

    let empty typ =
        {typ = typ; updates = UpdateTree.empty; defaultValue = None}

    let fillRegion defaultValue region =
        { region with defaultValue = Some defaultValue }

    let maxTime (tree : updateTree<'a, heapAddress, 'b>) startingTime =
        RegionTree.foldl (fun m _ {key=_; value=v} -> VectorTime.max m (timeOf v)) startingTime tree

    let read mr key isDefault instantiate =
        let makeSymbolic tree = instantiate mr.typ {typ=mr.typ; updates=tree; defaultValue = mr.defaultValue}
        let makeDefault () =
            match mr.defaultValue with
            | Some d -> d
            | None -> makeDefaultValue mr.typ
        UpdateTree.read key isDefault makeSymbolic makeDefault mr.updates

    let validateWrite value cellType =
        let typ = typeOf value
        canCastImplicitly typ cellType

    let memset mr keysAndValues =
        {typ = mr.typ; updates = UpdateTree.memset keysAndValues mr.updates; defaultValue = mr.defaultValue}

    let write mr key value =
        assert(validateWrite value mr.typ)
        {typ = mr.typ; updates = UpdateTree.write key value mr.updates; defaultValue = mr.defaultValue}

    let map (mapTerm : term -> term) (mapType : Type -> Type) (mapTime : vectorTime -> vectorTime) mr =
        {typ = mapType mr.typ; updates = UpdateTree.map (fun reg k -> k.Map mapTerm mapType mapTime reg) mapTerm mr.updates; defaultValue = Option.map mapTerm mr.defaultValue}

    let deterministicCompose earlier later =
        assert(later.defaultValue.IsNone)
        if earlier.typ = later.typ then
            if UpdateTree.isEmpty earlier.updates then { later with defaultValue = earlier.defaultValue }
            else {typ = earlier.typ; updates = UpdateTree.deterministicCompose earlier.updates later.updates; defaultValue = earlier.defaultValue}
        else internalfail "Composing two incomparable memory objects!"

    let compose earlier later =
        assert(later.defaultValue.IsNone)
        if later.updates |> UpdateTree.forall (fun k -> not k.key.IsUnion) then
            [(True, deterministicCompose earlier later)]
        elif earlier.typ = later.typ then
            UpdateTree.compose earlier.updates later.updates |> List.map (fun (g, tree) -> (g, {typ=earlier.typ; updates = tree; defaultValue = earlier.defaultValue}))
        else internalfail "Composing two incomparable memory objects!"

    let toString indent mr = UpdateTree.print indent toString mr.updates

    let flatten mr =
        RegionTree.foldr (fun _ k acc -> (k.key, k.value)::acc) [] mr.updates

    let localizeArray address dimension mr =
        let anyIndexRegion = List.replicate dimension points<int>.Universe |> listProductRegion<points<int>>.OfSeq
        let reg = productRegion<vectorTime intervals, int points listProductRegion>.ProductOf (MemoryKeyUtils.regionOfHeapAddress address) anyIndexRegion
        {typ=mr.typ; updates = UpdateTree.localize reg mr.updates; defaultValue = mr.defaultValue}

    // TODO: merging!

type memoryRegion<'key, 'reg when 'key : equality and 'key :> IMemoryKey<'key, 'reg> and 'reg : equality and 'reg :> IRegion<'reg>> with
    override x.ToString() = MemoryRegion.toString "" x

type setKeyWrapper<'key when 'key : equality> =
    {key : 'key}
    interface IRegionTreeKey<setKeyWrapper<'key>> with
        override x.Hides _ = false
    override x.ToString() = x.key.ToString()
type symbolicSet<'key, 'reg when 'key : equality and 'key :> IMemoryKey<'key, 'reg> and 'reg : equality and 'reg :> IRegion<'reg>> = regionTree<setKeyWrapper<'key>, 'reg>

module SymbolicSet =
    let empty<'key, 'reg when 'key : equality and 'key :> IMemoryKey<'key, 'reg> and 'reg : equality and 'reg :> IRegion<'reg>> =
        RegionTree.empty<setKeyWrapper<'key>, 'reg>
    let isEmpty s = RegionTree.isEmpty s
    let matchingElements x s =
        s |> RegionTree.localize (x :> IMemoryKey<'key, 'reg>).Region |> Node |> RegionTree.foldl (fun acc _ k -> k.key::acc) []
    let add x s =
        let matching = matchingElements x s
        match matching with
        | [x'] when x' = x -> s
        | _ ->
            RegionTree.write x.Region {key=x} s
    let ofSeq s = Seq.fold (fun s x -> add x s) empty s
    let map (mapTerm : term -> term) (mapType : Type -> Type) (mapTime : vectorTime -> vectorTime) (s : symbolicSet<'key, 'reg>) =
        s |> RegionTree.map (fun reg k -> let reg, k' = k.key.Map mapTerm mapType mapTime reg in (reg, k'.Region, {key=k'}))
    let union x y = RegionTree.foldr (fun _ k acc -> add k.key acc) x y
    let print s =
        let sb = s |> RegionTree.foldl (fun (sb : StringBuilder) _ -> toString >> sprintf "%s, " >> sb.Append) (StringBuilder().Append "{ ")
        (if sb.Length > 2 then sb.Remove(sb.Length - 2, 2) else sb).Append(" }").ToString()

type heapRegion = memoryRegion<heapAddressKey, vectorTime intervals>
type arrayRegion = memoryRegion<heapArrayIndexKey, productRegion<vectorTime intervals, int points listProductRegion>>
type vectorRegion = memoryRegion<heapVectorIndexKey, productRegion<vectorTime intervals, int points>>
type stackBufferRegion = memoryRegion<stackBufferIndexKey, int points>
type staticsRegion = memoryRegion<symbolicTypeKey, freeRegion<typeWrapper>>
type symbolicTypeSet = symbolicSet<symbolicTypeKey, freeRegion<typeWrapper>>
