-- NON-122 리파인: 장르 이름 영어 단일화 + 비-장르 제거 + 서브장르 심화.
-- 0034/0035 적용 후 실행. 장르명은 영어로만(name). Scene 축은 두지 않음 —
-- K-Pop·국가 성격은 후속 Country(MusicBrainz artist.country 자동)로 처리.

-- 1) 이름 단일화(영어). 기존 name_en(영어)이 name 이 됨.
alter table public.genres rename column name_en to name;
alter table public.genres drop column if exists name_ko;

-- 2) 비-장르 제거: K-Pop(지역/씬)·OST(형식)는 음악 장르가 아님. 태그 있으면 cascade.
delete from public.genres where slug in ('kpop', 'ost');

-- 3) 음악 서브장르 심화(복수 선택으로 다양하게).
insert into public.genres (slug, name, parent_id, sort_order)
select c.slug, c.name, p.id, c.sort_order
from (values
    ('grunge',     'Grunge',        'rock',       216),
    ('surfrock',   'Surf Rock',     'rock',       217),
    ('artpop',     'Art Pop',       'pop',        252),
    ('dreampop',   'Dream Pop',     'pop',        253),
    ('ambient',    'Ambient',       'electronic', 243),
    ('trance',     'Trance',        'electronic', 244),
    ('dnb',        'Drum & Bass',   'electronic', 245),
    ('dubstep',    'Dubstep',       'electronic', 246),
    ('synthwave',  'Synthwave',     'electronic', 247),
    ('drill',      'Drill',         'hiphop',     222),
    ('lofi',       'Lo-fi',         'hiphop',     223),
    ('bossa',      'Bossa Nova',    'jazz',       270),
    ('swing',      'Swing',         'jazz',       271),
    ('gospel',     'Gospel',        'soul',       280),
    ('deathmetal', 'Death Metal',   'metal',      261),
    ('blackmetal', 'Black Metal',   'metal',      262)
) as c(slug, name, parent_slug, sort_order)
join public.genres p on p.slug = c.parent_slug
on conflict (slug) do nothing;
