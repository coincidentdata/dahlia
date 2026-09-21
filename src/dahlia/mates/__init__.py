from ._base import Alignment, Mate
from .coincident import Coincident, coincident
from .concentric import Concentric, concentric
from .parallel import Parallel, parallel
from .perpendicular import Perpendicular, perpendicular
from .tangent import Tangent, tangent
from .lock import Lock, lock
from .distance import Distance, distance
from .angle import Angle, angle

MATE_MODELS = {model.model_fields["type"].default: model for model in (
    Coincident, Concentric, Parallel, Perpendicular, Tangent, Lock, Distance, Angle,
)}

__all__ = [
    "Mate", "Alignment", "MATE_MODELS",
    "Coincident", "coincident",
    "Concentric", "concentric",
    "Parallel", "parallel",
    "Perpendicular", "perpendicular",
    "Tangent", "tangent",
    "Lock", "lock",
    "Distance", "distance",
    "Angle", "angle",
]
