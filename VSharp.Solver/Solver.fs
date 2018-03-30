namespace VSharp

open Microsoft.Z3
open VSharp.Core

type public ISolver<'TAst, 'TResult> =
    abstract member Encode : term -> 'TAst
    abstract member Solve : 'TAst -> 'TResult

type public ISmtSolver<'TAst> =
    inherit ISolver<'TAst, SmtResult>

type public IZ3Solver =
    inherit ISmtSolver<AST>

type public IHornSolver<'TAst> =
    inherit ISolver<'TAst, HornResult>

type public Z3Solver() =
    interface IZ3Solver with
        member x.Encode t = Z3.encodeTerm t |> fst
        member x.Solve t = SmtUnsat

type public Z3Simplifier() =
    interface IPropositionalSimplifier with
        member x.Simplify t = Z3.simplifyPropositional t
