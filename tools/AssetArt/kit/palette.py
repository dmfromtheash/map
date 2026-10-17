"""One palette for every stamp, tuned against the regional ground (watercolour grass #8FB04E),
the river #3A7079 and the country road #AD9573 so objects sit in the same paint."""
from brush import hexc

GROUND = hexc('#759440')           # measured mean of watercolour-grass
GROUND_SHADOW = hexc('#3E5A2A')      # contact / cast shadows on grass
INK = hexc('#3A3226')                # warm dark brown, never black

LEAF = hexc('#5A8A3C'); LEAF_LIGHT = hexc('#A6C45E'); LEAF_SHADE = hexc('#2F5B45'); LEAF_INK = hexc('#22402F')
PINE = hexc('#3F6E4A'); PINE_LIGHT = hexc('#7FA35A'); PINE_SHADE = hexc('#264A3C')
AUTUMN = hexc('#B8573A'); AUTUMN_LIGHT = hexc('#DE8D52'); AUTUMN_SHADE = hexc('#7A3430')

ROCK_LIGHT = hexc('#E9E0CB'); ROCK = hexc('#C7BCA5'); ROCK_SHADE = hexc('#9A9082'); ROCK_DEEP = hexc('#716A61')
ROCK_INK = hexc('#433D37')
SNOW = hexc('#F4F1E8'); SNOW_SHADE = hexc('#B9C3C6')
SLOPE_GREEN = hexc('#7FA046'); SLOPE_OCHRE = hexc('#B9A462')
OCHRE = hexc('#CDA968'); OCHRE_SHADE = hexc('#9A7A48')

WATER = hexc('#4F8791'); WATER_LIGHT = hexc('#9CC8C4'); FOAM = hexc('#E2F2EE')
LAVA = hexc('#D9612E'); LAVA_HOT = hexc('#F2B04A')

WALL = hexc('#EADFC6'); WALL_SHADE = hexc('#BFAF93'); STONE = hexc('#CFC6B2'); STONE_SHADE = hexc('#978D7E')
TIMBER = hexc('#6B5039'); ROOF = hexc('#B5613F'); ROOF_LIGHT = hexc('#D38459'); ROOF_SHADE = hexc('#83412F')
THATCH = hexc('#C9A45E'); THATCH_SHADE = hexc('#94743F'); SLATE = hexc('#6E7A80'); SLATE_SHADE = hexc('#4C555A')
WINDOW = hexc('#3B3A36'); GLOW = hexc('#F0C66A'); WOOD = hexc('#8C6A47')
