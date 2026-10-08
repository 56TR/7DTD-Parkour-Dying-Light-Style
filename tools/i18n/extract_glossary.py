# Rebuilds tools/i18n/vanilla_glossary.json (the game's own translations of the terms the mod's texts use) from
# your 7 Days to Die install. The file is not in the repository: it is the game's text.
#   python -I tools/i18n/extract_glossary.py <mod root> "<game dir>"
import csv
import json
import os
import re
import sys

root, game = sys.argv[1], sys.argv[2]
rows = list(csv.reader(open(os.path.join(game, 'Data', 'Config', 'Localization.csv'), encoding='utf-8-sig')))
hdr = rows[0]
langs = ['english', 'german', 'spanish', 'french', 'italian', 'japanese', 'koreana', 'polish', 'brazilian', 'russian',
         'turkish', 'schinese', 'tchinese']
col = {l: hdr.index(l) for l in langs}
want = re.compile(r'^(perkParkour\w*|perkAgilityMastery(Name|Desc|Rank4Desc|Rank4LongDesc)|attAgility(Name|Desc)?'
                  r'|xuiComboOnOff(On|Off)|xuiOptionsGeneral(Crosshairs|CrosshairsEnabled|CrosshairsEnabledTooltip)?'
                  r'|xuiBtnOptionsGeneral|statStamina\w*|buffLegSprainedName|buffLegBrokenName|skillAgilityAthletics\w*'
                  r'|xuiBuffStatStamina|inpActPlayerCrouchName|inpActPlayerJumpName|inpActPlayerRunName|goZMSprint|lblKick'
                  r'|goFP|goTP|xuiZombies|xuiAnimals|statFallDamage|xuiWall|cameraTip_title|xuiPerk|xuiLevel)$')
out = {r[0]: {l: (r[col[l]] if col[l] < len(r) else '') for l in langs} for r in rows[1:] if r and want.match(r[0])}
path = os.path.join(root, 'tools', 'i18n', 'vanilla_glossary.json')
json.dump(out, open(path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(f'{len(out)} terms -> {path}')
