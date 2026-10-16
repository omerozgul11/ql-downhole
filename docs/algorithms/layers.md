# Layer interpretation

Code: `src/DownholeStudio.Analysis/Velocity/LayerModeler.cs`.

The layer model is a list of layer tops (the first at 0 m); each layer's bottom is the next top or
the model bottom (deepest usable receiver unless set). Editing operations — add, move, delete,
merge, split boundaries, set densities — are pure functions recorded as undoable edits.

## Suggested boundaries
Optional and never applied without confirmation:

1. Optimal piecewise-linear segmentation of corrected time versus depth for k = 1…8 segments by
   dynamic programming (each segment ≥ 3 receivers, cost = Σ squared residuals of a line fit).
2. k is chosen by the Bayesian information criterion n·ln(SSE/n) + (3k − 1)·ln n.
3. The largest segmentation not above the BIC choice in which adjacent segments differ in velocity
   by at least 15 % is kept (smaller changes are not resolvable layers).
4. Each boundary is placed at the intersection of the two adjacent fitted lines when it falls
   between the bracketing receivers, otherwise mid-way between them.

Each suggestion states the evidence (velocity above and below). With noisy picks, suggestions can be
misplaced; the analyst decides, ideally guided by borehole logs.
