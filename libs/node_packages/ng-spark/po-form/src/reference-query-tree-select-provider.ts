import { NodePage, NodeRequest, TreeNode, TreeSelectProvider } from '@mintplayer/ng-bootstrap/tree-select';
import { QueryResult, QueryResultItem } from '@mintplayer/ng-spark/models';

/** Rows per request: one "load more" step of the multi-reference panel. */
export const SPARK_REFERENCE_TREE_PAGE_SIZE = 50;

/** Runs one page of a reference query: `skip`/`take` and the optional search term. */
export type ReferenceQueryPageLoader = (page: { skip: number; take: number; search?: string }) => Promise<QueryResult>;

/** The chip and option label of a reference candidate: its breadcrumb, or its id when it has none. */
export function referenceTreeNode(item: QueryResultItem): TreeNode | null {
  return item.id ? { id: item.id, label: item.breadcrumb || item.id } : null;
}

/**
 * The `<bs-tree-select>` data port of a multi-reference, backed by the reference query itself.
 *
 * It replaced an `InMemoryTreeSelectProvider` over the rows the form pre-loaded, which were one page of
 * the query (50 by default): the panel listed and searched only those, so a candidate past the 50th
 * could not be chosen at all, and nothing said the list was cut (see `docs/default_index_PRD.md`, the
 * note beside D10). Here the panel's "load more" is the next server page and its search is the query's
 * `search`, so every row the caller may read is reachable, under the same row security as the list.
 */
export class ReferenceQueryTreeSelectProvider implements TreeSelectProvider {
  constructor(private readonly load: ReferenceQueryPageLoader, private readonly pageSize = SPARK_REFERENCE_TREE_PAGE_SIZE) {}

  loadRoots(req: NodeRequest): Promise<NodePage> {
    return this.page(req);
  }

  search(query: string, req: NodeRequest): Promise<NodePage> {
    return this.page(req, query.trim() || undefined);
  }

  /** A reference query is flat: no node has children. */
  loadChildren(_parentId: string, _req: NodeRequest): Promise<NodePage> {
    return Promise.resolve({ nodes: [] });
  }

  private async page(req: NodeRequest, search?: string): Promise<NodePage> {
    const skip = req.offset ?? 0;
    const result = await this.load({ skip, take: this.pageSize, search });
    const items = result.items ?? [];
    const nodes = items.map(referenceTreeNode).filter((n): n is TreeNode => n !== null);
    return { nodes, hasMore: skip + items.length < (result.totalItems ?? 0) };
  }
}
