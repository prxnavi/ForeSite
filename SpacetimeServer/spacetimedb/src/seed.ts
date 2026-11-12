// Seed data for the CBRE Digital Twin demo.
//
// Values are approximations compiled from public research (see DATA_SOURCES)
// and are meant to be swapped for live feeds (Zillow ZHVI / Redfin, NOAA SPC,
// USDA Web Soil Survey, county permit APIs) in production.

export type RoomSeed = {
  name: string;
  kind: string;
  floor: number;
  x: number;
  z: number;
  w: number;
  d: number;
  ceiling?: number;
  glazing?: number;
  flags?: string[];
};

export type ComponentSeed = {
  kind: string;
  name: string;
  material: string;
  room: string; // room name
  ox?: number;
  oz?: number;
  oy?: number;
  age: number; // years at seed time
  life: number; // expected service life (years)
  cost: number; // replacement / repair cost (USD)
  condition: number; // 0..1
  hazardFlag?: string;
};

export type BlueprintSeed = {
  id: number;
  name: string;
  tagline: string;
  locationId: number;
  style: string;
  stage: string;
  yearBuilt: number;
  floors: number;
  beds: number;
  baths: number;
  lotSqft: number;
  foundation: string;
  floorJoist: string;
  ceilingJoist: string;
  roofType: string;
  features: string[];
  description: string;
  accentColor: string;
  rooms: RoomSeed[];
  components: ComponentSeed[];
};

export const LOCATIONS = [
  {
    id: 1, name: 'Richardson (Canyon Creek)', city: 'Richardson', state: 'TX',
    medianPpsf: 236, yoyAppreciation: -0.021, daysOnMarket: 38, schoolRating: 7.5,
    hailRisk: 0.78, soilShrinkSwell: 0.82, floodRisk: 0.15, freezeRisk: 0.35, humidity: 0.55,
    walkScore: 42, propertyTaxRate: 0.0221,
    buyerTags: ['schools', 'home_office', 'kitchen_island', 'energy_efficient', 'two_car_garage'],
  },
  {
    id: 2, name: 'Plano (Willow Bend)', city: 'Plano', state: 'TX',
    medianPpsf: 247, yoyAppreciation: -0.015, daysOnMarket: 41, schoolRating: 8.6,
    hailRisk: 0.78, soilShrinkSwell: 0.76, floodRisk: 0.1, freezeRisk: 0.35, humidity: 0.55,
    walkScore: 38, propertyTaxRate: 0.0205,
    buyerTags: ['schools', 'game_room', 'home_office', 'three_car_garage', 'open_concept', 'pool'],
  },
  {
    id: 3, name: 'Dallas (Uptown / State-Thomas)', city: 'Dallas', state: 'TX',
    medianPpsf: 392, yoyAppreciation: -0.03, daysOnMarket: 56, schoolRating: 6.1,
    hailRisk: 0.7, soilShrinkSwell: 0.72, floodRisk: 0.2, freezeRisk: 0.35, humidity: 0.55,
    walkScore: 89, propertyTaxRate: 0.0223,
    buyerTags: ['walkability', 'rooftop_deck', 'smart_home', 'low_maintenance', 'balcony'],
  },
  {
    id: 4, name: 'Frisco (Fields)', city: 'Frisco', state: 'TX',
    medianPpsf: 268, yoyAppreciation: -0.009, daysOnMarket: 49, schoolRating: 9.1,
    hailRisk: 0.82, soilShrinkSwell: 0.7, floodRisk: 0.1, freezeRisk: 0.35, humidity: 0.55,
    walkScore: 28, propertyTaxRate: 0.0198,
    buyerTags: ['schools', 'pool', 'game_room', 'three_car_garage', 'ev_charger', 'energy_efficient', 'media_room', 'primary_down'],
  },
  {
    id: 5, name: 'Austin (Mueller)', city: 'Austin', state: 'TX',
    medianPpsf: 405, yoyAppreciation: -0.058, daysOnMarket: 62, schoolRating: 7.2,
    hailRisk: 0.52, soilShrinkSwell: 0.62, floodRisk: 0.3, freezeRisk: 0.3, humidity: 0.5,
    walkScore: 72, propertyTaxRate: 0.0198,
    buyerTags: ['walkability', 'energy_efficient', 'home_office', 'solar', 'ev_charger'],
  },
  {
    id: 6, name: 'Houston (The Heights)', city: 'Houston', state: 'TX',
    medianPpsf: 334, yoyAppreciation: 0.008, daysOnMarket: 46, schoolRating: 7.0,
    hailRisk: 0.42, soilShrinkSwell: 0.86, floodRisk: 0.68, freezeRisk: 0.2, humidity: 0.86,
    walkScore: 70, propertyTaxRate: 0.0231,
    buyerTags: ['walkability', 'covered_patio', 'energy_efficient', 'low_maintenance'],
  },
].map(l => ({
  ...l,
  dataSource:
    'Median $/sqft & DOM: Zillow ZHVI / Redfin Data Center snapshot (approx.); hail: NOAA SPC severe-report climatology; ' +
    'shrink-swell: USDA NRCS Web Soil Survey (Houston Black / Austin clays); flood: FEMA NFHL; freeze: Winter Storm Uri 2021 exposure; tax: county appraisal districts.',
}));

// Feature catalog: base national demand & typical value premium (% of price).
export const FEATURE_CATALOG: Record<string, { name: string; demand: number; pct: number; pitch: string; addCost?: number }> = {
  open_concept: { name: 'Open-concept great room', demand: 0.7, pct: 0.015, pitch: 'Sightlines from kitchen to living photograph beautifully and read as "modern" in listing photos.' },
  kitchen_island: { name: 'Kitchen island', demand: 0.75, pct: 0.01, pitch: 'Top-requested kitchen feature in NAR buyer surveys; anchors the entertaining story.', addCost: 6500 },
  home_office: { name: 'Dedicated home office', demand: 0.72, pct: 0.02, pitch: 'Hybrid-work buyers pay for a door that closes — market it as a 4th bedroom alternative.', addCost: 4500 },
  game_room: { name: 'Game / bonus room', demand: 0.55, pct: 0.015, pitch: 'Family buyers in North Texas suburbs filter for game rooms explicitly.' },
  media_room: { name: 'Media room', demand: 0.4, pct: 0.012, pitch: 'Pre-wired theater differentiates against comparable new builds.' },
  three_car_garage: { name: '3-car garage', demand: 0.6, pct: 0.025, pitch: 'Storage + EV + workshop — a hard filter for many suburban buyers.' },
  two_car_garage: { name: '2-car garage', demand: 0.5, pct: 0.005, pitch: 'Attached covered parking.' },
  pool: { name: 'Pool', demand: 0.45, pct: 0.03, pitch: 'In DFW summers a pool expands usable outdoor season to ~6 months.' },
  covered_patio: { name: 'Covered patio', demand: 0.65, pct: 0.012, pitch: 'Shaded outdoor living is a top-3 outdoor feature in Texas.', addCost: 18000 },
  primary_down: { name: 'Primary suite down', demand: 0.6, pct: 0.02, pitch: 'Appeals to move-up and downsizing buyers simultaneously.' },
  energy_efficient: { name: 'High-efficiency envelope', demand: 0.68, pct: 0.02, pitch: 'Spray foam + low-E glazing: quantify the lower utility bill in the listing.', addCost: 9000 },
  ev_charger: { name: 'EV charger (Level 2)', demand: 0.38, pct: 0.005, pitch: 'Low cost, high signal for tech-forward buyers.', addCost: 1800 },
  smart_home: { name: 'Smart-home package', demand: 0.5, pct: 0.008, pitch: 'Smart locks, thermostat, leak sensors — also lowers insurance risk.', addCost: 2500 },
  rooftop_deck: { name: 'Rooftop deck', demand: 0.6, pct: 0.04, pitch: 'Skyline views are the #1 differentiator for urban townhomes.' },
  walkability: { name: 'Walkable location', demand: 0.6, pct: 0.03, pitch: 'Walk Score 80+ — lead with restaurants and trail access.' },
  low_maintenance: { name: 'Low-maintenance living', demand: 0.55, pct: 0.01, pitch: 'Lock-and-leave appeal for professionals.' },
  walk_in_pantry: { name: 'Walk-in pantry', demand: 0.6, pct: 0.006, pitch: 'Consistently ranks in top kitchen wish-lists.' },
  mudroom: { name: 'Mudroom drop zone', demand: 0.5, pct: 0.006, pitch: 'Family-friendly organisation from garage entry.' },
  vaulted_ceilings: { name: 'Vaulted ceilings', demand: 0.45, pct: 0.008, pitch: 'Volume reads as luxury in photos and walkthroughs.' },
  balcony: { name: 'Private balcony', demand: 0.5, pct: 0.01, pitch: 'Outdoor space is scarce in urban product.' },
  large_lot: { name: 'Large mature lot', demand: 0.55, pct: 0.02, pitch: 'Lot size is irreplaceable — room to expand or add a pool.' },
  mature_trees: { name: 'Mature shade trees', demand: 0.45, pct: 0.01, pitch: 'Lower cooling loads and curb appeal.' },
  solar: { name: 'Solar PV', demand: 0.35, pct: 0.015, pitch: 'Owned (not leased) panels add value.', addCost: 22000 },
};

// Cost vs. Value (Zonda / JLC 2024 national averages, approx.)
export const UPGRADE_CATALOG = [
  { name: 'Garage door replacement', cost: 4302, recoup: 1.94, feature: '' },
  { name: 'Entry door replacement (steel)', cost: 2355, recoup: 1.88, feature: '' },
  { name: 'Manufactured stone veneer', cost: 11066, recoup: 1.53, feature: '' },
  { name: 'Minor kitchen remodel (midrange)', cost: 27492, recoup: 0.96, feature: 'kitchen_island' },
  { name: 'Fiber-cement siding replacement', cost: 20619, recoup: 0.89, feature: '' },
  { name: 'Wood deck addition', cost: 17615, recoup: 0.83, feature: 'covered_patio' },
  { name: 'Bath remodel (midrange)', cost: 25251, recoup: 0.74, feature: '' },
  { name: 'Vinyl window replacement', cost: 21264, recoup: 0.67, feature: 'energy_efficient' },
];

export const CASCADE_RULES: Array<[string, string, number, string, string]> = [
  ['gutters', 'foundation', 0.4, 'Overflowing gutters saturate perimeter soil; expansive clay heaves unevenly', 'USDA NRCS shrink-swell; Foundation Performance Association (Houston)'],
  ['gutters', 'roof', 0.2, 'Standing water rots fascia and decking at eaves', 'InterNACHI roof inspection SOP'],
  ['roof', 'attic_insulation', 0.55, 'Roof leak saturates attic insulation, collapsing R-value', 'DOE Building America moisture guide'],
  ['roof', 'drywall', 0.35, 'Water intrusion stains and softens ceiling drywall', 'IBHS hail & wind research'],
  ['attic_insulation', 'hvac', 0.25, 'Lost R-value spikes cooling load and short-cycles the condenser', 'ASHRAE Handbook — Fundamentals'],
  ['foundation', 'plumbing', 0.45, 'Slab heave/settlement shears under-slab supply & drain lines', 'Post-Tensioning Institute; TX foundation repair claims'],
  ['foundation', 'drywall', 0.5, 'Differential movement cracks drywall and racks door frames', 'FEMA P-58 fragility (drywall partitions)'],
  ['foundation', 'windows', 0.3, 'Racked openings break IGU seals and jam sashes', 'FEMA P-58 fragility (glazing)'],
  ['plumbing', 'drywall', 0.5, 'Concealed leak wets wall cavities', 'Insurance Information Institute — water damage claims'],
  ['plumbing', 'mold', 0.6, 'Hidden moisture > 48h triggers mold growth', 'EPA Mold Remediation guidance'],
  ['drywall', 'mold', 0.4, 'Wet gypsum is a primary mold substrate', 'EPA Mold Remediation guidance'],
  ['water_heater', 'plumbing', 0.2, 'Pressure / T&P failures stress the supply system', 'NAHB Life Expectancy of Home Components'],
  ['water_heater', 'mold', 0.35, 'Tank rupture floods garage/utility spaces', 'IBHS water heater failure study'],
  ['hvac', 'mold', 0.35, 'Without dehumidification indoor RH climbs above 60%', 'ASHRAE 62.2 / EPA indoor air'],
  ['electrical', 'hvac', 0.15, 'Breaker failures and voltage sag damage compressor motors', 'CPSC — Federal Pacific Stab-Lok investigation'],
  ['sewer', 'plumbing', 0.3, 'Root intrusion / bellied lines back up into the drain system', 'NASSCO pipeline condition grading'],
  ['sewer', 'mold', 0.2, 'Sewage backup contaminates finishes', 'IICRC S500 water damage standard'],
];

// Processing order is a topological sort of the cascade DAG above.
export const KIND_ORDER = [
  'gutters', 'sewer', 'water_heater', 'electrical', 'roof', 'attic_insulation',
  'foundation', 'windows', 'plumbing', 'hvac', 'drywall', 'mold',
];

export const SENSOR_SPECS: Record<string, { label: string; unit: string; base: number; warn: number; crit: number }> = {
  roof: { label: 'Attic sheathing moisture', unit: '% MC', base: 9, warn: 16, crit: 20 },
  foundation: { label: 'Slab crack width', unit: 'mm', base: 0.3, warn: 3, crit: 6 },
  plumbing: { label: 'Static pressure drop', unit: 'psi/hr', base: 0.1, warn: 2, crit: 5 },
  electrical: { label: 'Panel thermal rise', unit: '°C', base: 4, warn: 15, crit: 30 },
  hvac: { label: 'Runtime vs. design', unit: '%', base: 85, warn: 120, crit: 150 },
  water_heater: { label: 'Anode depletion', unit: '%', base: 10, warn: 70, crit: 90 },
  windows: { label: 'IGU seal failures', unit: '% units', base: 0, warn: 20, crit: 40 },
  gutters: { label: 'Overflow events', unit: '/month', base: 0, warn: 3, crit: 6 },
  attic_insulation: { label: 'R-value deficit', unit: '%', base: 5, warn: 30, crit: 50 },
  drywall: { label: 'Wall moisture (WME)', unit: '%', base: 10, warn: 17, crit: 25 },
  mold: { label: 'Indoor relative humidity', unit: '% RH', base: 45, warn: 60, crit: 70 },
  sewer: { label: 'Flow restriction', unit: '%', base: 2, warn: 30, crit: 60 },
};

export function sensorFromCondition(kind: string, condition: number): number {
  const s = SENSOR_SPECS[kind];
  if (!s) return 0;
  const stress = Math.pow(Math.max(0, 1 - condition), 1.2);
  return Math.round((s.base + (s.crit - s.base) * stress * 1.15) * 10) / 10;
}

export const DATA_SOURCES = [
  ['Structural', 'International Residential Code 2021 (R502.3 joist spans, R502.3.3 cantilevers, R602.10 wall bracing, R305 ceiling height, R310 egress, R403.1.8 expansive soils)', 'Span, load-path, bracing, egress & foundation checks', 'https://codes.iccsafe.org/content/IRC2021P2'],
  ['Structural', 'American Wood Council — Span Tables for Joists & Rafters (SYP #2, 40 psf LL, L/360)', 'Allowable clear spans per joist size', 'https://awc.org/publications/span-tables/'],
  ['Structural', 'FEMA P-58 Seismic Performance Assessment — component fragility functions', 'Drywall / glazing damage propagation weights', 'https://femap58.atcouncil.org/'],
  ['Lifecycle', 'NAHB / Bank of America — Study of Life Expectancy of Home Components; InterNACHI life-expectancy chart', 'Expected service life per building system (Weibull scale)', 'https://www.nachi.org/life-expectancy.htm'],
  ['Hazard', 'USDA NRCS Web Soil Survey — shrink-swell (linear extensibility) for Houston Black clay', 'Foundation & under-slab plumbing hazard multiplier', 'https://websoilsurvey.nrcs.usda.gov/'],
  ['Hazard', 'NOAA Storm Prediction Center — severe hail report climatology', 'Roof hazard multiplier (DFW is a top-5 US hail corridor)', 'https://www.spc.noaa.gov/climo/'],
  ['Hazard', 'FEMA National Flood Hazard Layer', 'Flood exposure for location comparison', 'https://www.fema.gov/flood-maps/national-flood-hazard-layer'],
  ['Safety', 'CPSC investigation — Federal Pacific Electric Stab-Lok breakers', 'Electrical panel hazard flag (insurers frequently decline)', 'https://www.cpsc.gov/'],
  ['Claims', 'Insurance Information Institute — homeowners claims frequency & severity (water damage / freezing)', 'Loss severity calibration for plumbing, drywall & mold', 'https://www.iii.org/fact-statistic/facts-statistics-homeowners-and-renters-insurance'],
  ['Market', 'Zillow Home Value Index & Redfin Data Center', 'Median $/sqft, YoY appreciation, days on market by submarket', 'https://www.zillow.com/research/data/'],
  ['Market', 'Zonda / JLC Cost vs. Value Report 2024', 'Upgrade cost & resale recoup %', 'https://www.costvsvalue.com/'],
  ['Market', 'NAR Profile of Home Buyers & Sellers / NAHB What Home Buyers Really Want', 'Feature demand weights', 'https://www.nar.realtor/research-and-statistics'],
];

export const BLUEPRINTS: BlueprintSeed[] = [
  {
    id: 1,
    name: 'Canyon Creek Ranch',
    tagline: '1979 single-story ranch — classic DFW resale with hidden deferred maintenance',
    locationId: 1, style: 'Mid-century ranch', stage: 'existing', yearBuilt: 1979, floors: 1,
    beds: 3, baths: 2, lotSqft: 9800,
    foundation: 'Slab-on-grade (conventionally reinforced)', floorJoist: 'none', ceilingJoist: '2x6',
    roofType: 'Asphalt 3-tab shingle on 2x6 rafters',
    features: ['covered_patio', 'large_lot', 'mature_trees', 'two_car_garage'],
    description: 'Original-owner ranch on Houston Black clay. Strong lot and school zone; roof, panel and windows are near end of life.',
    accentColor: '#FF8A5B',
    rooms: [
      { name: 'Living Room', kind: 'living', floor: 0, x: 0, z: 0, w: 20, d: 16, glazing: 0.35 },
      { name: 'Kitchen', kind: 'kitchen', floor: 0, x: 20, z: 0, w: 14, d: 13, glazing: 0.25 },
      { name: 'Dining', kind: 'dining', floor: 0, x: 20, z: 13, w: 14, d: 9, glazing: 0.3 },
      { name: 'Hall', kind: 'hall', floor: 0, x: 0, z: 16, w: 20, d: 4, glazing: 0 },
      { name: 'Bedroom 2', kind: 'bedroom', floor: 0, x: 0, z: 20, w: 11, d: 12, glazing: 0.25 },
      { name: 'Hall Bath', kind: 'bath', floor: 0, x: 11, z: 20, w: 9, d: 7, glazing: 0.1 },
      { name: 'Laundry', kind: 'laundry', floor: 0, x: 11, z: 27, w: 9, d: 5, glazing: 0 },
      { name: 'Primary Bedroom', kind: 'primary', floor: 0, x: 20, z: 22, w: 15, d: 14, glazing: 0.25 },
      { name: 'Primary Bath', kind: 'bath', floor: 0, x: 35, z: 22, w: 9, d: 10, glazing: 0.1 },
      { name: 'Bedroom 3', kind: 'bedroom', floor: 0, x: 0, z: 32, w: 11, d: 11, glazing: 0.25 },
      { name: 'Den', kind: 'office', floor: 0, x: 11, z: 32, w: 9, d: 11, glazing: 0.2 },
      { name: 'Garage', kind: 'garage', floor: 0, x: 34, z: 0, w: 23, d: 22, ceiling: 9, glazing: 0 },
      { name: 'Covered Patio', kind: 'patio', floor: 0, x: 0, z: 43, w: 20, d: 10, glazing: 0 },
    ],
    components: [
      { kind: 'roof', name: 'Roof covering', material: 'Asphalt 3-tab shingles (2nd layer)', room: 'Living Room', ox: 0.5, oz: 0.5, oy: 10, age: 17, life: 20, cost: 14500, condition: 0.35 },
      { kind: 'gutters', name: 'Gutters & downspouts', material: 'Seamless aluminum', room: 'Bedroom 3', ox: 0.0, oz: 1.0, oy: 8.5, age: 21, life: 25, cost: 2600, condition: 0.4 },
      { kind: 'foundation', name: 'Slab foundation', material: 'Conventional slab on expansive clay', room: 'Hall', ox: 0.5, oz: 0.5, oy: 0.2, age: 47, life: 75, cost: 18500, condition: 0.45 },
      { kind: 'plumbing', name: 'Supply & under-slab drains', material: 'Galvanized supply / cast-iron drains', room: 'Hall Bath', ox: 0.3, oz: 0.5, oy: 1, age: 47, life: 50, cost: 12500, condition: 0.5 },
      { kind: 'sewer', name: 'Sewer lateral', material: 'Cast iron to clay tile', room: 'Laundry', ox: 0.5, oz: 1.0, oy: 0.2, age: 47, life: 50, cost: 9800, condition: 0.45 },
      { kind: 'electrical', name: 'Main electrical panel', material: '100A Federal Pacific Stab-Lok', room: 'Garage', ox: 0.1, oz: 0.9, oy: 5, age: 47, life: 40, cost: 3800, condition: 0.4, hazardFlag: 'Federal Pacific Stab-Lok panel — breakers documented to fail to trip (CPSC); many insurers decline coverage' },
      { kind: 'hvac', name: 'HVAC split system', material: '3.5-ton AC + gas furnace (R-22)', room: 'Laundry', ox: 0.8, oz: 0.2, oy: 6, age: 14, life: 15, cost: 11800, condition: 0.55, hazardFlag: 'R-22 refrigerant phased out — recharge cost is high' },
      { kind: 'water_heater', name: 'Water heater', material: '40 gal gas tank', room: 'Garage', ox: 0.9, oz: 0.9, oy: 3, age: 11, life: 10, cost: 2200, condition: 0.5 },
      { kind: 'windows', name: 'Windows', material: 'Single-pane aluminum', room: 'Living Room', ox: 0.0, oz: 0.5, oy: 4, age: 47, life: 30, cost: 18500, condition: 0.3 },
      { kind: 'attic_insulation', name: 'Attic insulation', material: 'Blown fiberglass ~R-19 (settled)', room: 'Kitchen', ox: 0.5, oz: 0.5, oy: 8.8, age: 47, life: 40, cost: 2800, condition: 0.45 },
      { kind: 'drywall', name: 'Interior finishes', material: '1/2" gypsum, plaster texture', room: 'Primary Bedroom', ox: 0.5, oz: 0.0, oy: 5, age: 47, life: 70, cost: 6200, condition: 0.7 },
      { kind: 'mold', name: 'Moisture / indoor air', material: 'No mechanical ventilation', room: 'Primary Bath', ox: 0.5, oz: 0.5, oy: 7, age: 10, life: 15, cost: 7800, condition: 0.7 },
    ],
  },
  {
    id: 2,
    name: 'Willow Bend Two-Story',
    tagline: '2006 open-concept family home — a load-bearing wall was removed',
    locationId: 2, style: 'Traditional two-story', stage: 'existing', yearBuilt: 2006, floors: 2,
    beds: 4, baths: 2.5, lotSqft: 8200,
    foundation: 'Post-tensioned slab', floorJoist: '2x10', ceilingJoist: 'truss',
    roofType: 'Architectural shingle (hail replacement 2016)',
    features: ['open_concept', 'kitchen_island', 'home_office', 'game_room', 'three_car_garage', 'walk_in_pantry', 'mudroom'],
    description: 'Prior owner opened the great room by removing a bearing wall without an engineered beam. Primary suite cantilevers 4 ft beyond the slab.',
    accentColor: '#5B8CFF',
    rooms: [
      { name: 'Great Room', kind: 'living', floor: 0, x: 0, z: 0, w: 24, d: 22, ceiling: 10, glazing: 0.45, flags: ['removed_wall'] },
      { name: 'Kitchen', kind: 'kitchen', floor: 0, x: 24, z: 0, w: 16, d: 16, ceiling: 10, glazing: 0.25 },
      { name: 'Breakfast Nook', kind: 'dining', floor: 0, x: 24, z: 16, w: 16, d: 6, ceiling: 10, glazing: 0.6 },
      { name: 'Office', kind: 'office', floor: 0, x: 0, z: 22, w: 12, d: 12, ceiling: 10, glazing: 0.3 },
      { name: 'Powder', kind: 'bath', floor: 0, x: 12, z: 22, w: 6, d: 6, ceiling: 10 },
      { name: 'Mudroom', kind: 'mudroom', floor: 0, x: 12, z: 28, w: 6, d: 6, ceiling: 10 },
      { name: 'Stair Hall', kind: 'stair', floor: 0, x: 18, z: 22, w: 6, d: 12, ceiling: 10 },
      { name: 'Laundry', kind: 'laundry', floor: 0, x: 24, z: 22, w: 8, d: 12, ceiling: 10 },
      { name: 'Walk-in Pantry', kind: 'pantry', floor: 0, x: 32, z: 22, w: 8, d: 12, ceiling: 10 },
      { name: '3-Car Garage', kind: 'garage', floor: 0, x: 40, z: 0, w: 32, d: 24, ceiling: 10 },
      { name: 'Bedroom 2', kind: 'bedroom', floor: 1, x: 0, z: 0, w: 13, d: 12, glazing: 0.25 },
      { name: 'Bedroom 3', kind: 'bedroom', floor: 1, x: 13, z: 0, w: 12, d: 12, glazing: 0.25 },
      { name: 'Game Room', kind: 'game', floor: 1, x: 25, z: 0, w: 15, d: 16, glazing: 0.3 },
      { name: 'Upper Hall', kind: 'hall', floor: 1, x: 0, z: 12, w: 25, d: 6 },
      { name: 'Hall Bath', kind: 'bath', floor: 1, x: 0, z: 18, w: 8, d: 6, glazing: 0.1 },
      { name: 'Linen', kind: 'closet', floor: 1, x: 8, z: 18, w: 10, d: 6 },
      { name: 'Stair Landing', kind: 'stair', floor: 1, x: 18, z: 18, w: 6, d: 6 },
      { name: 'Bedroom 4', kind: 'bedroom', floor: 1, x: 25, z: 16, w: 15, d: 12, glazing: 0.25 },
      { name: 'Primary Suite', kind: 'primary', floor: 1, x: 0, z: 24, w: 16, d: 14, glazing: 0.3 },
      { name: 'Primary Bath', kind: 'bath', floor: 1, x: 16, z: 24, w: 9, d: 10, glazing: 0.15 },
    ],
    components: [
      { kind: 'roof', name: 'Roof covering', material: 'Architectural shingle', room: 'Game Room', ox: 0.5, oz: 0.5, oy: 18, age: 10, life: 22, cost: 16500, condition: 0.78 },
      { kind: 'gutters', name: 'Gutters & downspouts', material: 'Aluminum', room: 'Primary Suite', ox: 0.0, oz: 1.0, oy: 17, age: 20, life: 25, cost: 2900, condition: 0.62 },
      { kind: 'foundation', name: 'Post-tension slab', material: 'PT slab on expansive clay', room: 'Great Room', ox: 0.5, oz: 0.5, oy: 0.2, age: 20, life: 80, cost: 16500, condition: 0.68 },
      { kind: 'plumbing', name: 'Supply & drains', material: 'PEX supply / PVC drains', room: 'Kitchen', ox: 0.8, oz: 0.2, oy: 1, age: 20, life: 45, cost: 9500, condition: 0.85 },
      { kind: 'sewer', name: 'Sewer lateral', material: 'PVC', room: 'Laundry', ox: 0.5, oz: 1.0, oy: 0.2, age: 20, life: 80, cost: 8500, condition: 0.9 },
      { kind: 'electrical', name: 'Main electrical panel', material: '200A Square D', room: '3-Car Garage', ox: 0.05, oz: 0.5, oy: 5, age: 20, life: 40, cost: 3500, condition: 0.9 },
      { kind: 'hvac', name: 'HVAC (2 zones)', material: '2 × 3-ton SEER 13', room: 'Laundry', ox: 0.5, oz: 0.3, oy: 7, age: 18, life: 15, cost: 16800, condition: 0.32 },
      { kind: 'water_heater', name: 'Water heater', material: '50 gal gas tank', room: '3-Car Garage', ox: 0.95, oz: 0.9, oy: 3, age: 7, life: 10, cost: 2400, condition: 0.78 },
      { kind: 'windows', name: 'Windows', material: 'Vinyl double-pane', room: 'Great Room', ox: 0.0, oz: 0.5, oy: 4, age: 20, life: 25, cost: 21000, condition: 0.58 },
      { kind: 'attic_insulation', name: 'Attic insulation', material: 'Blown fiberglass R-30', room: 'Bedroom 4', ox: 0.5, oz: 0.5, oy: 9, age: 20, life: 40, cost: 3200, condition: 0.8 },
      { kind: 'drywall', name: 'Interior finishes', material: '1/2" gypsum', room: 'Great Room', ox: 0.5, oz: 0.0, oy: 6, age: 20, life: 70, cost: 7500, condition: 0.72 },
      { kind: 'mold', name: 'Moisture / indoor air', material: 'Bath fans only', room: 'Primary Bath', ox: 0.5, oz: 0.5, oy: 7, age: 8, life: 15, cost: 8200, condition: 0.78 },
    ],
  },
  {
    id: 3,
    name: 'State-Thomas Townhome',
    tagline: '2015 urban 3-story with rooftop deck — water heater & roof membrane at risk',
    locationId: 3, style: 'Contemporary townhome', stage: 'existing', yearBuilt: 2015, floors: 4,
    beds: 2, baths: 2.5, lotSqft: 1400,
    foundation: 'Slab-on-grade with drilled piers', floorJoist: '2x12', ceilingJoist: 'truss',
    roofType: 'Low-slope TPO membrane with pedestal-paver roof deck',
    features: ['rooftop_deck', 'smart_home', 'kitchen_island', 'walkability', 'low_maintenance', 'two_car_garage', 'balcony'],
    description: 'Lock-and-leave townhome three blocks from McKinney Ave. Rooftop deck membrane has never been inspected; tank water heater is past service life.',
    accentColor: '#A16BFF',
    rooms: [
      { name: 'Garage', kind: 'garage', floor: 0, x: 0, z: 0, w: 20, d: 22, ceiling: 9 },
      { name: 'Flex Office', kind: 'office', floor: 0, x: 0, z: 22, w: 20, d: 14, glazing: 0.4 },
      { name: 'Kitchen', kind: 'kitchen', floor: 1, x: 0, z: 0, w: 20, d: 16, ceiling: 10, glazing: 0.3 },
      { name: 'Living', kind: 'living', floor: 1, x: 0, z: 16, w: 20, d: 20, ceiling: 10, glazing: 0.6, flags: ['beam'] },
      { name: 'Balcony', kind: 'deck', floor: 1, x: 0, z: 36, w: 20, d: 6, ceiling: 0, flags: ['beam'] },
      { name: 'Primary Bedroom', kind: 'primary', floor: 2, x: 0, z: 0, w: 14, d: 16, glazing: 0.35 },
      { name: 'Primary Bath', kind: 'bath', floor: 2, x: 14, z: 0, w: 6, d: 16 },
      { name: 'Hall', kind: 'hall', floor: 2, x: 0, z: 16, w: 20, d: 6 },
      { name: 'Bedroom 2', kind: 'bedroom', floor: 2, x: 0, z: 22, w: 12, d: 14, glazing: 0.35 },
      { name: 'Bath 2', kind: 'bath', floor: 2, x: 12, z: 22, w: 8, d: 8 },
      { name: 'Laundry', kind: 'laundry', floor: 2, x: 12, z: 30, w: 8, d: 6 },
      { name: 'Stair Penthouse', kind: 'stair', floor: 3, x: 0, z: 0, w: 8, d: 10, glazing: 0.5 },
      { name: 'Rooftop Deck', kind: 'deck', floor: 3, x: 0, z: 10, w: 20, d: 24, ceiling: 0 },
    ],
    components: [
      { kind: 'roof', name: 'Roof deck membrane', material: 'TPO under pedestal pavers', room: 'Rooftop Deck', ox: 0.5, oz: 0.5, oy: 0.5, age: 11, life: 18, cost: 19500, condition: 0.52 },
      { kind: 'gutters', name: 'Scuppers & drains', material: 'Through-wall scuppers', room: 'Rooftop Deck', ox: 1.0, oz: 1.0, oy: 0.3, age: 11, life: 20, cost: 3200, condition: 0.55 },
      { kind: 'foundation', name: 'Slab on drilled piers', material: 'Pier-and-beam reinforced slab', room: 'Flex Office', ox: 0.5, oz: 0.5, oy: 0.2, age: 11, life: 90, cost: 22000, condition: 0.92 },
      { kind: 'plumbing', name: 'Supply & drains', material: 'PEX / PVC', room: 'Bath 2', ox: 0.5, oz: 0.5, oy: 1, age: 11, life: 45, cost: 11000, condition: 0.86 },
      { kind: 'sewer', name: 'Sewer lateral', material: 'PVC to city main', room: 'Garage', ox: 0.5, oz: 0.0, oy: 0.2, age: 11, life: 80, cost: 9000, condition: 0.9 },
      { kind: 'electrical', name: 'Main electrical panel', material: '200A Eaton w/ smart breakers', room: 'Garage', ox: 0.95, oz: 0.8, oy: 5, age: 11, life: 40, cost: 3600, condition: 0.95 },
      { kind: 'hvac', name: 'HVAC (heat pump)', material: '2.5-ton heat pump, attic air handler', room: 'Laundry', ox: 0.5, oz: 0.5, oy: 8, age: 11, life: 15, cost: 10500, condition: 0.6 },
      { kind: 'water_heater', name: 'Water heater', material: '50 gal electric tank (3rd floor!)', room: 'Laundry', ox: 0.2, oz: 0.8, oy: 3, age: 11, life: 10, cost: 2600, condition: 0.28, hazardFlag: 'Tank located above two finished floors — a rupture cascades downward' },
      { kind: 'windows', name: 'Windows', material: 'Aluminum-clad low-E', room: 'Living', ox: 0.5, oz: 1.0, oy: 4, age: 11, life: 30, cost: 24000, condition: 0.85 },
      { kind: 'attic_insulation', name: 'Roof insulation', material: 'Polyiso under membrane', room: 'Primary Bedroom', ox: 0.5, oz: 0.5, oy: 9, age: 11, life: 40, cost: 4200, condition: 0.82 },
      { kind: 'drywall', name: 'Interior finishes', material: '5/8" gypsum, level-5', room: 'Living', ox: 0.0, oz: 0.5, oy: 6, age: 11, life: 70, cost: 8200, condition: 0.9 },
      { kind: 'mold', name: 'Moisture / indoor air', material: 'ERV ventilation', room: 'Primary Bath', ox: 0.5, oz: 0.5, oy: 7, age: 11, life: 15, cost: 7800, condition: 0.74 },
    ],
  },
  {
    id: 4,
    name: 'Fields Modern Farmhouse',
    tagline: '2026 new build in framing — construction manager prototype',
    locationId: 4, style: 'Modern farmhouse', stage: 'under_construction', yearBuilt: 2026, floors: 2,
    beds: 4, baths: 3.5, lotSqft: 11500,
    foundation: 'Post-tensioned slab on engineered piers', floorJoist: 'I-joist', ceilingJoist: 'truss',
    roofType: 'Standing-seam metal + Class 4 impact shingles',
    features: ['open_concept', 'vaulted_ceilings', 'kitchen_island', 'primary_down', 'home_office', 'game_room', 'media_room', 'three_car_garage', 'pool', 'covered_patio', 'ev_charger', 'energy_efficient', 'smart_home', 'mudroom'],
    description: 'Spec home at framing stage. 18-ft vaulted family room with a 70% glazed rear wall; 26-ft covered patio on a glulam beam; media room over the garage.',
    accentColor: '#2EC4B6',
    rooms: [
      { name: 'Family Room', kind: 'living', floor: 0, x: 0, z: 0, w: 26, d: 20, ceiling: 18, glazing: 0.7, flags: ['vaulted'] },
      { name: 'Kitchen', kind: 'kitchen', floor: 0, x: 26, z: 0, w: 18, d: 16, ceiling: 10, glazing: 0.3 },
      { name: 'Dining', kind: 'dining', floor: 0, x: 26, z: 16, w: 18, d: 10, ceiling: 10, glazing: 0.4 },
      { name: 'Primary Suite', kind: 'primary', floor: 0, x: 0, z: 20, w: 16, d: 16, ceiling: 11, glazing: 0.3 },
      { name: 'Primary Bath', kind: 'bath', floor: 0, x: 16, z: 20, w: 10, d: 12, ceiling: 10, glazing: 0.15 },
      { name: 'Office', kind: 'office', floor: 0, x: 26, z: 26, w: 12, d: 10, ceiling: 10, glazing: 0.35 },
      { name: 'Mudroom', kind: 'mudroom', floor: 0, x: 38, z: 26, w: 6, d: 10, ceiling: 10 },
      { name: '3-Car Garage', kind: 'garage', floor: 0, x: 44, z: 0, w: 34, d: 24, ceiling: 10 },
      { name: 'Covered Patio', kind: 'patio', floor: 0, x: 0, z: -14, w: 26, d: 14, ceiling: 11, flags: ['beam'] },
      { name: 'Pool', kind: 'pool', floor: 0, x: 2, z: -34, w: 22, d: 14, ceiling: 0 },
      { name: 'Game Room', kind: 'game', floor: 1, x: 26, z: 0, w: 18, d: 16, glazing: 0.3 },
      { name: 'Bedroom 2', kind: 'bedroom', floor: 1, x: 26, z: 16, w: 12, d: 12, glazing: 0.3 },
      { name: 'Bedroom 3', kind: 'bedroom', floor: 1, x: 38, z: 16, w: 12, d: 12, glazing: 0.3 },
      { name: 'Jack-and-Jill Bath', kind: 'bath', floor: 1, x: 26, z: 28, w: 10, d: 8 },
      { name: 'Media Room', kind: 'media', floor: 1, x: 44, z: 0, w: 18, d: 16 },
    ],
    components: [
      { kind: 'roof', name: 'Roof dry-in', material: 'Synthetic underlayment (temporary dry-in)', room: 'Game Room', ox: 0.5, oz: 0.5, oy: 18, age: 0.3, life: 2, cost: 9500, condition: 0.85, hazardFlag: 'Temporary dry-in exposed to spring hail season — schedule metal panels before April' },
      { kind: 'gutters', name: 'Site drainage', material: 'Temporary grading / silt fence', room: 'Primary Suite', ox: 0.0, oz: 1.0, oy: 0.5, age: 0.3, life: 2, cost: 4500, condition: 0.7 },
      { kind: 'foundation', name: 'PT slab on piers', material: '5" PT slab, 24 engineered piers', room: 'Family Room', ox: 0.5, oz: 0.5, oy: 0.2, age: 0.5, life: 90, cost: 26000, condition: 0.93 },
      { kind: 'plumbing', name: 'Rough-in plumbing', material: 'PEX-A manifold / PVC', room: 'Kitchen', ox: 0.7, oz: 0.3, oy: 1, age: 0.2, life: 50, cost: 14000, condition: 0.95 },
      { kind: 'sewer', name: 'Sewer tap', material: 'PVC SDR-35', room: '3-Car Garage', ox: 0.1, oz: 0.0, oy: 0.2, age: 0.4, life: 80, cost: 9500, condition: 0.96 },
      { kind: 'electrical', name: 'Temp power & panel', material: '400A service, temp pole', room: '3-Car Garage', ox: 0.05, oz: 0.8, oy: 5, age: 0.3, life: 40, cost: 5200, condition: 0.92 },
      { kind: 'hvac', name: 'HVAC (pending set)', material: '2 × variable-speed heat pump', room: 'Mudroom', ox: 0.5, oz: 0.5, oy: 8, age: 0.0, life: 18, cost: 24000, condition: 0.98 },
      { kind: 'water_heater', name: 'Water heater', material: 'Tankless gas 199k BTU', room: '3-Car Garage', ox: 0.9, oz: 0.9, oy: 4, age: 0.0, life: 20, cost: 4200, condition: 0.98 },
      { kind: 'windows', name: 'Window package', material: 'Black-frame fiberglass low-E (on order)', room: 'Family Room', ox: 0.5, oz: 0.0, oy: 6, age: 0.0, life: 35, cost: 42000, condition: 0.9, hazardFlag: 'Supply-chain lead time 9 weeks — openings protected with temporary sheathing' },
      { kind: 'attic_insulation', name: 'Insulation', material: 'Open-cell spray foam (scheduled)', room: 'Bedroom 2', ox: 0.5, oz: 0.5, oy: 9, age: 0.0, life: 50, cost: 9800, condition: 0.97 },
      { kind: 'drywall', name: 'Framing & sheathing moisture', material: 'SPF studs, OSB sheathing', room: 'Family Room', ox: 1.0, oz: 0.5, oy: 6, age: 0.4, life: 70, cost: 12000, condition: 0.78, hazardFlag: 'Framing exposed to rain — moisture must be < 19% MC before drywall (IRC R702.3)' },
      { kind: 'mold', name: 'Moisture / indoor air', material: 'ERV + dehumidifier (spec)', room: 'Primary Bath', ox: 0.5, oz: 0.5, oy: 7, age: 0.0, life: 15, cost: 7800, condition: 0.85 },
    ],
  },
];

// The ranch plan is drawn on a 10% compressed grid; scale it to its true ~1,780 sqft footprint.
BLUEPRINTS[0].rooms = BLUEPRINTS[0].rooms.map(r => ({
  ...r, x: Math.round(r.x * 1.1 * 10) / 10, z: Math.round(r.z * 1.1 * 10) / 10,
  w: Math.round(r.w * 1.1 * 10) / 10, d: Math.round(r.d * 1.1 * 10) / 10,
}));
