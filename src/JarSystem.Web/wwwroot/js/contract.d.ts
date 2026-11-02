// 本文件由 `dotnet run --project test\JarSystem.Test -c Release -- --dump-schema` 生成，请勿手改。
// 来源：后端 DTO 的 [JsonPropertyName]（state.schema.json）
// 用法（前端仍是普通脚本，无需打包器）：在 jsconfig.json 的 checkJs 下用 JSDoc 引用，例如
//   /** @type {StateDto} */ let state = null;

interface StateDto {
  workshops?: WorkshopStateDto[];
  orders?: OrderStateDto[];
  warehouse?: WarehouseStateDto;
  producerStatus?: { producers?: { name?: string; rates?: Record<string, number> }[]; generators?: { shape?: string; rate?: number; accumulated?: number; producers?: number }[]; warehouse?: { jars?: number; liquids?: Record<string, number> } };
  globalState?: GlobalStateDto;
  eventHistory?: Record<string, unknown>[];
  worldRevision?: number;
  processes?: ProcessStateDto[];
  turns?: { used?: number; limit?: number; exhausted?: boolean };
  level?: { goalOrders?: number; completedOrders?: number; outcome?: string; reason?: string };
}

interface WorkshopStateDto {
  index?: number;
  name?: string;
  baseMarks?: string[];
  additionalMarks?: string[];
  ban?: string[];
  maxJars?: number;
  maxLiquids?: number;
  jars?: JarStateDto[];
  liquids?: Record<string, number>;
  canPourShapes?: string[];
  isMaster?: boolean;
  isSlave?: boolean;
}

interface JarStateDto {
  id?: number | null;
  shape?: string;
  liquid?: string | null;
  isMarked?: boolean;
  description?: string;
}

interface OrderStateDto {
  index?: number;
  id?: number;
  type?: string;
  requirements?: ({ shape?: string; liquid?: string | null })[];
  status?: string;
  feasibilityResult?: FeasibilityResultData | null;
  description?: string;
}

interface FeasibilityResultData {
  feasible?: boolean;
  undetermined?: boolean;
  blockage?: string | null;
  reason?: string | null;
  timestamp?: string | null;
  directCount?: number;
  operationCount?: number;
  solverPath?: SolverStepDto[] | null;
  masterPath?: SolverStepDto[] | null;
  checkMethod?: string | null;
  transferJarIds?: number[] | null;
  transferLiquids?: Record<string, number> | null;
}

interface WarehouseStateDto {
  jars?: number;
  liquids?: Record<string, number>;
  discardedJars?: number;
  discardedLiquids?: Record<string, number>;
}

interface GlobalStateDto {
  autoCheckEnabled?: boolean;
  currentMaster?: number | null;
  currentSlave?: number | null;
  masterName?: string | null;
  slaveName?: string | null;
}

interface SolverStepDto {
  step?: number;
  requirement?: string | null;
  operation?: string | null;
  type?: string | null;
  jarIndex?: number | null;
  jarId?: number | null;
  jarShape?: string | null;
  jarLiquid?: string | null;
  targetShape?: string | null;
  targetLiquid?: string | null;
  costLiquid?: string | null;
  reshapeMode?: string | null;
  consumed?: Record<string, number> | null;
  jarsAfter?: string[];
}

interface AutoCheckResultDto {
  index?: number;
  orderId?: number;
  result?: FeasibilityResultData | null;
}

interface WorldMetaDto {
  shapes?: string[];
  liquids?: string[];
}

interface ProcessStateDto {
  id?: number;
  kind?: string;
  subject?: string;
  subjectId?: number | null;
  state?: string;
  outcome?: string | null;
  currentStep?: number;
  totalSteps?: number;
  startedAtTick?: number | null;
  deadlineTick?: number | null;
  remainingTicks?: number | null;
  blockedReason?: string | null;
  lastJournalSeq?: number;
}

type CommandName = "getState" | "createOrder" | "createRandomOrder" | "createWorkshop" | "createRandomWorkshop" | "updateWorkshopCapacity" | "setWorkshopCapability" | "moveOrder" | "setMaster" | "setSlave" | "executeOrder" | "deleteOrder" | "checkFeasibility" | "checkAllFeasibility" | "manualTick" | "reset" | "restartLevel" | "updateProducerRate" | "startTick" | "stopTick" | "setAutoCheck" | "startOrder" | "cancelProcess" | "setTickInterval";
type MessageType = "init" | "state" | "orderExecuted" | "workshopSwitched" | "feasibilityResult" | "allFeasibilityResults" | "autoCheckResults" | "autoCheckChanged" | "error" | "world" | "frame" | "tickIntervalChanged";

interface RequirementPayload { shape: string; liquid?: string | null; }

/** `createOrder` 的载荷 */
interface CreateOrderPayload {
  requirements?: RequirementPayload[];
  orderType?: "A" | "B";
}

/** `createWorkshop` 的载荷 */
interface CreateWorkshopPayload {
  name?: string;
  baseMarks?: string[];
  additionalMarks?: string[];
  ban?: string[];
}

/** `updateWorkshopCapacity` 的载荷 */
interface UpdateWorkshopCapacityPayload {
  index?: number;
  maxJars?: number | null;
  maxLiquids?: number | null;
}

/** `setWorkshopCapability` 的载荷 */
interface SetWorkshopCapabilityPayload {
  index?: number;
  baseMarks?: string[];
  additionalMarks?: string[];
  ban?: string[];
}

/** `moveOrder` 的载荷 */
interface MoveOrderPayload {
  orderId?: number;
  dir?: number;
}

/** `setMaster` 的载荷 */
interface SetMasterPayload {
  index?: number;
}

/** `setSlave` 的载荷 */
interface SetSlavePayload {
  index?: number;
}

/** `executeOrder` 的载荷 */
interface ExecuteOrderPayload {
  orderId?: number;
  orderIndex?: number;
}

/** `startOrder` 的载荷 */
interface StartOrderPayload {
  orderId?: number;
  orderIndex?: number;
}

/** `cancelProcess` 的载荷 */
interface CancelProcessPayload {
  processId?: number;
}

/** `setTickInterval` 的载荷 */
interface SetTickIntervalPayload {
  intervalMs?: number;
}

/** `checkFeasibility` 的载荷 */
interface CheckFeasibilityPayload {
  orderId?: number;
  orderIndex?: number;
}

/** `deleteOrder` 的载荷 */
interface DeleteOrderPayload {
  orderId?: number;
  orderIndex?: number;
}

/** `updateProducerRate` 的载荷 */
interface UpdateProducerRatePayload {
  producerName?: string;
  shape?: string;
  rate?: number;
}

/** `setAutoCheck` 的载荷 */
interface SetAutoCheckPayload {
  enabled?: boolean;
}

/** `createRandomOrder` 的载荷 */
interface CreateRandomOrderPayload {
}

/** `createRandomWorkshop` 的载荷 */
interface CreateRandomWorkshopPayload {
}

/** `getState` 的载荷 */
interface GetStatePayload {
}

/** `restartLevel` 的载荷 */
interface RestartLevelPayload {
}

/** `checkAllFeasibility` 的载荷 */
interface CheckAllFeasibilityPayload {
}

/** `manualTick` 的载荷 */
interface ManualTickPayload {
}

/** `reset` 的载荷 */
interface ResetPayload {
}

/** `startTick` 的载荷 */
interface StartTickPayload {
}

/** `stopTick` 的载荷 */
interface StopTickPayload {
}

/** 命令名 → 载荷类型 的映射（给 send(type, data) 这类帮助函数用） */
interface CommandPayloadMap {
  "createOrder": CreateOrderPayload;
  "createWorkshop": CreateWorkshopPayload;
  "updateWorkshopCapacity": UpdateWorkshopCapacityPayload;
  "setWorkshopCapability": SetWorkshopCapabilityPayload;
  "moveOrder": MoveOrderPayload;
  "setMaster": SetMasterPayload;
  "setSlave": SetSlavePayload;
  "executeOrder": ExecuteOrderPayload;
  "startOrder": StartOrderPayload;
  "cancelProcess": CancelProcessPayload;
  "setTickInterval": SetTickIntervalPayload;
  "checkFeasibility": CheckFeasibilityPayload;
  "deleteOrder": DeleteOrderPayload;
  "updateProducerRate": UpdateProducerRatePayload;
  "setAutoCheck": SetAutoCheckPayload;
  "createRandomOrder": CreateRandomOrderPayload;
  "createRandomWorkshop": CreateRandomWorkshopPayload;
  "getState": GetStatePayload;
  "restartLevel": RestartLevelPayload;
  "checkAllFeasibility": CheckAllFeasibilityPayload;
  "manualTick": ManualTickPayload;
  "reset": ResetPayload;
  "startTick": StartTickPayload;
  "stopTick": StopTickPayload;
}
